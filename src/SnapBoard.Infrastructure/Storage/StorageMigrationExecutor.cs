using System.Security.Cryptography;
using System.Text;
using SnapBoard.Application.Storage;
using SnapBoard.Infrastructure.Persistence;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Infrastructure.Storage;

public enum StorageMigrationExecutionStatus
{
    Completed = 0,
    RolledBack = 1,
}

public sealed record StorageMigrationExecutionResult(
    StorageMigrationExecutionStatus Status,
    string MigrationId,
    string? BackupDirectory,
    string? ErrorCode);

public sealed class StorageMigrationExecutor
{
    private readonly IStoragePlatformService _platformService;
    private readonly TimeSpan _startupAcknowledgementPollInterval;
    private readonly TimeSpan _startupAcknowledgementTimeout;
    private readonly TimeProvider _timeProvider;

    public StorageMigrationExecutor(
        IStoragePlatformService platformService,
        TimeProvider? timeProvider = null,
        TimeSpan? startupAcknowledgementTimeout = null,
        TimeSpan? startupAcknowledgementPollInterval = null)
    {
        _platformService = platformService ?? throw new ArgumentNullException(nameof(platformService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupAcknowledgementTimeout = startupAcknowledgementTimeout ?? TimeSpan.FromSeconds(45);
        _startupAcknowledgementPollInterval =
            startupAcknowledgementPollInterval ?? TimeSpan.FromMilliseconds(200);
        if (_startupAcknowledgementTimeout <= TimeSpan.Zero ||
            _startupAcknowledgementPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startupAcknowledgementTimeout),
                "Startup acknowledgement timing must be positive.");
        }
    }

    public async ValueTask<StorageMigrationExecutionResult> ExecuteAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string canonicalManifestPath = Path.GetFullPath(manifestPath);
        string bootstrapDirectory = Path.GetDirectoryName(canonicalManifestPath) ??
            throw new StorageMetadataException("The migration manifest has no parent directory.");
        string applicationDataDirectory = Directory.GetParent(bootstrapDirectory)?.FullName ??
            throw new StorageMetadataException("The bootstrap directory has no parent.");
        StorageBootstrapPaths bootstrapPaths = StorageBootstrapPaths.Create(applicationDataDirectory);
        await using StorageLocationStore locationStore = new(bootstrapPaths, _platformService);
        StorageMigrationManifest manifest = await locationStore
            .ReadManifestAsync(canonicalManifestPath, cancellationToken)
            .ConfigureAwait(false) ?? throw new StorageMetadataException(
                "The migration manifest is missing.");
        ValidateManifest(manifest, canonicalManifestPath, bootstrapPaths);

        StorageMigrationStateDocument state = await locationStore
            .ReadMigrationStateAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new StorageMetadataException(
                "The migration state is missing.");
        ValidateInitialState(state, manifest);

        bool mainProcessExited = false;
        bool destinationPromoted = false;
        bool locatorSwitched = false;
        StorageProcessIdentity? launchedProcess = null;
        string? stagingDirectory = null;
        StorageLocationDocument? originalLocation = null;
        try
        {
            StorageProcessIdentity executorProcess = _platformService.GetCurrentProcessIdentity();
            if (!PathEquals(
                    executorProcess.ExecutablePath,
                    manifest.MigratorExecutablePath) ||
                !string.Equals(
                    executorProcess.UserIdentity,
                    manifest.MainProcess.UserIdentity,
                    StringComparison.Ordinal))
            {
                throw new StorageMetadataException(
                    "The migration helper process identity does not match manifest.");
            }

            state = await TransitionAsync(
                    locationStore,
                    state,
                    StorageMigrationPhase.WaitingForMainProcessExit,
                    cancellationToken)
                .ConfigureAwait(false);
            await _platformService.WaitForProcessExitAsync(
                    manifest.MainProcess,
                    cancellationToken)
                .ConfigureAwait(false);
            mainProcessExited = true;

            await using FileStream migrationLock = await AcquireMigrationLockAsync(
                    bootstrapPaths.MigrationLockPath,
                    manifest.MigrationId,
                    cancellationToken)
                .ConfigureAwait(false);
            originalLocation = await locationStore.ReadLocationAsync(cancellationToken)
                .ConfigureAwait(false) ?? throw new StorageMetadataException(
                    "The storage locator is missing.");
            ValidateLocator(originalLocation, manifest);

            state = await TransitionAsync(
                    locationStore,
                    state,
                    StorageMigrationPhase.ValidatingSource,
                    cancellationToken)
                .ConfigureAwait(false);
            StoragePathInspection sourceInspection = await _platformService.InspectPathAsync(
                    manifest.SourceDataRoot,
                    probeWriteCapabilities: false,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateSourceInspection(sourceInspection, manifest);
            StorageInstanceDocument sourceInstance = await locationStore
                .ReadInstanceMarkerAsync(manifest.SourceDataRoot, cancellationToken)
                .ConfigureAwait(false) ?? throw new StorageMetadataException(
                    "The source storage instance marker is missing.");
            if (!string.Equals(
                    sourceInstance.StorageInstanceId,
                    manifest.StorageInstanceId,
                    StringComparison.Ordinal))
            {
                throw new StorageMetadataException("The source storage instance does not match.");
            }

            SnapBoardStoragePaths sourcePaths = SnapBoardStoragePaths.Create(
                manifest.SourceDataRoot);
            StorageDatabaseSnapshot sourceSnapshot = await StorageDatabaseVerifier
                .CheckpointAndVerifyAsync(sourcePaths, cancellationToken)
                .ConfigureAwait(false);

            StoragePathInspection targetInspection = await _platformService.InspectPathAsync(
                    manifest.TargetDataRoot,
                    probeWriteCapabilities: true,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateTargetInspection(targetInspection, manifest);
            EnsureDirectoryEmpty(manifest.TargetDataRoot);

            stagingDirectory = GetStagingDirectory(manifest.TargetDataRoot, manifest.MigrationId);
            EnsureOwnedStagingDoesNotExist(stagingDirectory);
            await _platformService.EnsurePrivateDirectoryAsync(
                    stagingDirectory,
                    StorageDirectorySecurityMode.EmptyDirectoryOnly,
                    cancellationToken)
                .ConfigureAwait(false);
            state = await TransitionAsync(
                    locationStore,
                    state,
                    StorageMigrationPhase.CopyingToStaging,
                    cancellationToken)
                .ConfigureAwait(false);
            await CopyDataSetAsync(
                    sourcePaths,
                    SnapBoardStoragePaths.Create(stagingDirectory),
                    manifest.RequiredBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            state = await TransitionAsync(
                    locationStore,
                    state,
                    StorageMigrationPhase.VerifyingDestination,
                    cancellationToken)
                .ConfigureAwait(false);
            StorageInstanceDocument stagedInstance = await locationStore
                .ReadInstanceMarkerAsync(stagingDirectory, cancellationToken)
                .ConfigureAwait(false) ?? throw new StorageMetadataException(
                    "The staged storage instance marker is missing.");
            if (!string.Equals(
                    stagedInstance.StorageInstanceId,
                    manifest.StorageInstanceId,
                    StringComparison.Ordinal))
            {
                throw new StorageMetadataException("The staged storage instance does not match.");
            }

            SnapBoardStoragePaths stagingPaths = SnapBoardStoragePaths.Create(stagingDirectory);
            StorageDatabaseSnapshot destinationSnapshot = await StorageDatabaseVerifier
                .VerifyDestinationAsync(stagingPaths, cancellationToken)
                .ConfigureAwait(false);
            if (sourceSnapshot != destinationSnapshot)
            {
                throw new StorageMetadataException("The staged database summary does not match source.");
            }

            DeleteRuntimeDatabaseFiles(stagingPaths.DatabasePath);
            EnsureDirectoryEmpty(manifest.TargetDataRoot);
            state = await TransitionAsync(
                    locationStore,
                    state,
                    StorageMigrationPhase.SwitchingLocation,
                    cancellationToken)
                .ConfigureAwait(false);
            Directory.Delete(manifest.TargetDataRoot, recursive: false);
            Directory.Move(stagingDirectory, manifest.TargetDataRoot);
            destinationPromoted = true;
            stagingDirectory = null;

            StorageLocationDocument switchedLocation = originalLocation with
            {
                CurrentDataRoot = manifest.TargetDataRoot,
                VolumeIdentity = manifest.TargetVolumeIdentity,
                LastMigrationId = manifest.MigrationId,
                RollbackDataRoot = manifest.SourceDataRoot,
                Integrity = string.Empty,
            };
            await locationStore.WriteLocationAsync(switchedLocation, cancellationToken)
                .ConfigureAwait(false);
            locatorSwitched = true;
            state = await TransitionAsync(
                    locationStore,
                    state with { LocatorSwitched = true },
                    StorageMigrationPhase.StartingMainApplication,
                    cancellationToken)
                .ConfigureAwait(false);
            state = await TransitionAsync(
                    locationStore,
                    state,
                    StorageMigrationPhase.WaitingForStartupAcknowledgement,
                    cancellationToken)
                .ConfigureAwait(false);
            launchedProcess = await _platformService.StartProcessAsync(
                    manifest.MainExecutablePath,
                    [
                        "--migration-id",
                        manifest.MigrationId,
                        "--storage-bootstrap-root",
                        bootstrapPaths.ApplicationDataDirectory,
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            StorageStartupAcknowledgementDocument acknowledgement =
                await WaitForStartupAcknowledgementAsync(
                        locationStore,
                        manifest,
                        launchedProcess,
                        cancellationToken)
                    .ConfigureAwait(false);
            ValidateStartupAcknowledgement(acknowledgement, manifest, launchedProcess);

            string backupDirectory = await PreserveSourceBackupAsync(
                    bootstrapPaths,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            StorageLocationDocument completedLocation = switchedLocation with
            {
                LastMigrationCompletedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                RollbackDataRoot = backupDirectory,
                Integrity = string.Empty,
            };
            await locationStore.WriteLocationAsync(completedLocation, cancellationToken)
                .ConfigureAwait(false);
            state = await TransitionAsync(
                    locationStore,
                    state with { StartupAcknowledged = true },
                    StorageMigrationPhase.Completed,
                    cancellationToken)
                .ConfigureAwait(false);
            TryDelete(canonicalManifestPath);
            TryDelete(bootstrapPaths.GetStartupAcknowledgementPath(manifest.MigrationId));
            return new StorageMigrationExecutionResult(
                StorageMigrationExecutionStatus.Completed,
                manifest.MigrationId,
                backupDirectory,
                ErrorCode: null);
        }
        catch (Exception exception)
        {
            string errorCode = ClassifyError(exception);
            if (launchedProcess is not null)
            {
                await TryStopProcessAsync(launchedProcess).ConfigureAwait(false);
            }

            bool rollbackSucceeded = await TryRollbackAsync(
                    locationStore,
                    manifest,
                    state,
                    originalLocation,
                    destinationPromoted,
                    locatorSwitched,
                    stagingDirectory,
                    errorCode)
                .ConfigureAwait(false);
            if (mainProcessExited)
            {
                await TryRestartOriginalApplicationAsync(manifest, bootstrapPaths)
                    .ConfigureAwait(false);
            }

            if (!rollbackSucceeded)
            {
                throw new StorageMetadataException(
                    "Storage migration failed and automatic rollback was incomplete.",
                    exception);
            }

            return new StorageMigrationExecutionResult(
                StorageMigrationExecutionStatus.RolledBack,
                manifest.MigrationId,
                BackupDirectory: null,
                errorCode);
        }
    }

    private void ValidateManifest(
        StorageMigrationManifest manifest,
        string manifestPath,
        StorageBootstrapPaths bootstrapPaths)
    {
        if (manifest.FormatVersion != StorageDocumentVersions.MigrationManifest ||
            !StorageManagementService.IsManifestFresh(manifest))
        {
            throw new StorageMetadataException("The migration manifest is expired or unsupported.");
        }

        StorageLocationStore.ValidateIdentifier(manifest.MigrationId, nameof(manifest.MigrationId));
        StorageLocationStore.ValidateIdentifier(
            manifest.StorageInstanceId,
            nameof(manifest.StorageInstanceId));
        if (!PathEquals(manifestPath, bootstrapPaths.GetManifestPath(manifest.MigrationId)) ||
            !PathEquals(manifest.BootstrapDirectory, bootstrapPaths.BootstrapDirectory) ||
            PathEquals(manifest.SourceDataRoot, manifest.TargetDataRoot) ||
            IsAncestorOrDescendant(manifest.SourceDataRoot, manifest.TargetDataRoot) ||
            manifest.RequiredBytes < 0 ||
            !PathEquals(manifest.MainExecutablePath, manifest.MainProcess.ExecutablePath))
        {
            throw new StorageMetadataException("The migration manifest fields are invalid.");
        }

        ValidateExecutableFile(manifest.MainExecutablePath);
        ValidateExecutableFile(manifest.MigratorExecutablePath);
    }

    private static void ValidateExecutableFile(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath) ||
            (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StorageMetadataException(
                "A migration executable is missing or uses a reparse point.");
        }
    }

    private void ValidateInitialState(
        StorageMigrationStateDocument state,
        StorageMigrationManifest manifest)
    {
        if (state.FormatVersion != StorageDocumentVersions.MigrationState ||
            state.Phase != StorageMigrationPhase.Requested ||
            state.LocatorSwitched ||
            state.StartupAcknowledged ||
            !string.Equals(state.MigrationId, manifest.MigrationId, StringComparison.Ordinal) ||
            !string.Equals(
                state.StorageInstanceId,
                manifest.StorageInstanceId,
                StringComparison.Ordinal) ||
            !PathEquals(state.SourceDataRoot, manifest.SourceDataRoot) ||
            !PathEquals(state.TargetDataRoot, manifest.TargetDataRoot))
        {
            throw new StorageMetadataException("The migration state does not match manifest.");
        }
    }

    private void ValidateLocator(
        StorageLocationDocument location,
        StorageMigrationManifest manifest)
    {
        if (location.FormatVersion != StorageDocumentVersions.Location ||
            !string.Equals(
                location.StorageInstanceId,
                manifest.StorageInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                location.VolumeIdentity,
                manifest.SourceVolumeIdentity,
                StringComparison.OrdinalIgnoreCase) ||
            !PathEquals(location.CurrentDataRoot, manifest.SourceDataRoot))
        {
            throw new StorageMetadataException("The active storage locator changed after request.");
        }
    }

    private void ValidateSourceInspection(
        StoragePathInspection inspection,
        StorageMigrationManifest manifest)
    {
        if (inspection.VolumeKind != StorageVolumeKind.Fixed ||
            inspection.ContainsReparsePoint ||
            !string.Equals(
                inspection.VolumeIdentity,
                manifest.SourceVolumeIdentity,
                StringComparison.OrdinalIgnoreCase) ||
            !PathEquals(inspection.CanonicalPath, manifest.SourceDataRoot))
        {
            throw new StorageMetadataException("The source volume identity is invalid.");
        }
    }

    private void ValidateTargetInspection(
        StoragePathInspection inspection,
        StorageMigrationManifest manifest)
    {
        if (inspection.VolumeKind != StorageVolumeKind.Fixed ||
            inspection.ContainsReparsePoint ||
            !inspection.IsPrivateToCurrentUser ||
            !inspection.SupportsWriteThroughAndAtomicRename ||
            inspection.AvailableBytes < manifest.RequiredBytes ||
            !string.Equals(
                inspection.VolumeIdentity,
                manifest.TargetVolumeIdentity,
                StringComparison.OrdinalIgnoreCase) ||
            !PathEquals(inspection.CanonicalPath, manifest.TargetDataRoot))
        {
            throw new StorageMetadataException("The target volume no longer satisfies migration requirements.");
        }
    }

    private static async ValueTask<FileStream> AcquireMigrationLockAsync(
        string path,
        string migrationId,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StorageMetadataException("The migration lock cannot be a reparse point.");
        }

        FileStream stream = new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        try
        {
            byte[] identifier = Encoding.ASCII.GetBytes(migrationId);
            stream.SetLength(0);
            await stream.WriteAsync(identifier, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            CryptographicOperations.ZeroMemory(identifier);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask CopyDataSetAsync(
        SnapBoardStoragePaths source,
        SnapBoardStoragePaths destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        long copiedBytes = 0;
        copiedBytes = await CopyFileAsync(
                source.DatabasePath,
                destination.DatabasePath,
                copiedBytes,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
        copiedBytes = await CopyFileAsync(
                Path.Combine(source.RootDirectory, "storage-instance.json"),
                Path.Combine(destination.RootDirectory, "storage-instance.json"),
                copiedBytes,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
        copiedBytes = await CopyDirectoryAsync(
                source.BlobDirectory,
                destination.BlobDirectory,
                copiedBytes,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
        _ = await CopyDirectoryAsync(
                source.RecoveryDirectory,
                destination.RecoveryDirectory,
                copiedBytes,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<long> CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        long copiedBytes,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
            return copiedBytes;
        }

        Stack<(string Source, string Destination)> pending = new();
        pending.Push((sourceDirectory, destinationDirectory));
        while (pending.TryPop(out (string Source, string Destination) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(current.Source);
            Directory.CreateDirectory(current.Destination);
            foreach (string sourceFile in Directory.EnumerateFiles(current.Source))
            {
                EnsureNotReparsePoint(sourceFile);
                string destinationFile = Path.Combine(
                    current.Destination,
                    Path.GetFileName(sourceFile));
                copiedBytes = await CopyFileAsync(
                        sourceFile,
                        destinationFile,
                        copiedBytes,
                        maximumBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (string sourceChild in Directory.EnumerateDirectories(current.Source))
            {
                EnsureNotReparsePoint(sourceChild);
                pending.Push((
                    sourceChild,
                    Path.Combine(current.Destination, Path.GetFileName(sourceChild))));
            }
        }

        return copiedBytes;
    }

    private static async ValueTask<long> CopyFileAsync(
        string sourcePath,
        string destinationPath,
        long copiedBytes,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(sourcePath);
        long length = new FileInfo(sourcePath).Length;
        long nextTotal = checked(copiedBytes + length);
        if (nextTotal > maximumBytes)
        {
            throw new StorageMetadataException("The copied data exceeded the manifest size bound.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return nextTotal;
    }

    private async ValueTask<StorageStartupAcknowledgementDocument>
        WaitForStartupAcknowledgementAsync(
            StorageLocationStore locationStore,
            StorageMigrationManifest manifest,
            StorageProcessIdentity launchedProcess,
            CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow() + _startupAcknowledgementTimeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageStartupAcknowledgementDocument? acknowledgement = await locationStore
                .ReadStartupAcknowledgementAsync(manifest.MigrationId, cancellationToken)
                .ConfigureAwait(false);
            if (acknowledgement is not null)
            {
                return acknowledgement;
            }

            await Task.Delay(
                    _startupAcknowledgementPollInterval,
                    _timeProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The migrated application did not acknowledge startup.");
    }

    private void ValidateStartupAcknowledgement(
        StorageStartupAcknowledgementDocument acknowledgement,
        StorageMigrationManifest manifest,
        StorageProcessIdentity launchedProcess)
    {
        if (acknowledgement.FormatVersion != StorageDocumentVersions.StartupAcknowledgement ||
            !string.Equals(
                acknowledgement.MigrationId,
                manifest.MigrationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                acknowledgement.StorageInstanceId,
                manifest.StorageInstanceId,
                StringComparison.Ordinal) ||
            acknowledgement.Process.ProcessId != launchedProcess.ProcessId ||
            acknowledgement.Process.StartTimeUtcTicks != launchedProcess.StartTimeUtcTicks ||
            !PathEquals(
                acknowledgement.Process.ExecutablePath,
                launchedProcess.ExecutablePath) ||
            !string.Equals(
                acknowledgement.Process.UserIdentity,
                launchedProcess.UserIdentity,
                StringComparison.Ordinal))
        {
            throw new StorageMetadataException("The startup acknowledgement identity is invalid.");
        }
    }

    private async ValueTask<string> PreserveSourceBackupAsync(
        StorageBootstrapPaths bootstrapPaths,
        StorageMigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        string sourceRoot = Path.GetFullPath(manifest.SourceDataRoot);
        if (IsSameOrDescendant(bootstrapPaths.BootstrapDirectory, sourceRoot))
        {
            string backupRoot = Path.Combine(
                bootstrapPaths.ApplicationDataDirectory,
                "storage-backups",
                $"{_timeProvider.GetUtcNow():yyyyMMdd-HHmmss}-{manifest.MigrationId}");
            await _platformService.EnsurePrivateDirectoryAsync(
                    backupRoot,
                    StorageDirectorySecurityMode.EmptyDirectoryOnly,
                    cancellationToken)
                .ConfigureAwait(false);
            List<(string Source, string Destination)> moved = [];
            try
            {
                foreach (string name in new[]
                {
                    "snapboard.db",
                    "blobs",
                    "recovery",
                    "storage-instance.json",
                })
                {
                    string source = Path.Combine(sourceRoot, name);
                    if (!File.Exists(source) && !Directory.Exists(source))
                    {
                        continue;
                    }

                    string destination = Path.Combine(backupRoot, name);
                    if (File.Exists(source))
                    {
                        File.Move(source, destination);
                    }
                    else
                    {
                        Directory.Move(source, destination);
                    }

                    moved.Add((source, destination));
                }
            }
            catch
            {
                foreach ((string source, string destination) in moved.AsEnumerable().Reverse())
                {
                    if (File.Exists(destination))
                    {
                        File.Move(destination, source);
                    }
                    else if (Directory.Exists(destination))
                    {
                        Directory.Move(destination, source);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(backupRoot).Any())
                {
                    Directory.Delete(backupRoot);
                }

                throw;
            }

            return backupRoot;
        }

        string siblingBackup = $"{sourceRoot}.backup-{manifest.MigrationId}";
        if (Directory.Exists(siblingBackup) || File.Exists(siblingBackup))
        {
            throw new StorageMetadataException("The source backup destination already exists.");
        }

        Directory.Move(sourceRoot, siblingBackup);
        return siblingBackup;
    }

    private async ValueTask<bool> TryRollbackAsync(
        StorageLocationStore locationStore,
        StorageMigrationManifest manifest,
        StorageMigrationStateDocument state,
        StorageLocationDocument? originalLocation,
        bool destinationPromoted,
        bool locatorSwitched,
        string? stagingDirectory,
        string errorCode)
    {
        try
        {
            state = await TransitionAsync(
                    locationStore,
                    state,
                    StorageMigrationPhase.RollingBack,
                    CancellationToken.None,
                    errorCode)
                .ConfigureAwait(false);
            if (locatorSwitched)
            {
                if (originalLocation is null || !Directory.Exists(manifest.SourceDataRoot))
                {
                    throw new StorageMetadataException("The original storage root is unavailable.");
                }

                await locationStore.WriteLocationAsync(
                        originalLocation with { Integrity = string.Empty },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (destinationPromoted)
            {
                QuarantineFailedTarget(manifest.TargetDataRoot, manifest.MigrationId);
            }

            if (stagingDirectory is not null)
            {
                DeleteOwnedDirectory(stagingDirectory);
            }

            _ = await TransitionAsync(
                    locationStore,
                    state with { LocatorSwitched = false },
                    StorageMigrationPhase.RolledBack,
                    CancellationToken.None,
                    errorCode)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            try
            {
                _ = await TransitionAsync(
                        locationStore,
                        state,
                        StorageMigrationPhase.Failed,
                        CancellationToken.None,
                        $"rollback-{errorCode}")
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            return false;
        }
    }

    private async ValueTask TryStopProcessAsync(StorageProcessIdentity process)
    {
        try
        {
            await _platformService.StopProcessAsync(process, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async ValueTask TryRestartOriginalApplicationAsync(
        StorageMigrationManifest manifest,
        StorageBootstrapPaths bootstrapPaths)
    {
        try
        {
            _ = await _platformService.StartProcessAsync(
                    manifest.MainExecutablePath,
                    ["--storage-bootstrap-root", bootstrapPaths.ApplicationDataDirectory],
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async ValueTask<StorageMigrationStateDocument> TransitionAsync(
        StorageLocationStore locationStore,
        StorageMigrationStateDocument state,
        StorageMigrationPhase phase,
        CancellationToken cancellationToken,
        string? errorCode = null)
    {
        StorageMigrationStateDocument updated = state with
        {
            Phase = phase,
            UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            ErrorCode = errorCode ?? state.ErrorCode,
            Integrity = string.Empty,
        };
        await locationStore.WriteMigrationStateAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }

    private static string GetStagingDirectory(string targetRoot, string migrationId)
    {
        string canonicalTarget = Path.GetFullPath(targetRoot);
        string parent = Path.GetDirectoryName(canonicalTarget) ??
            throw new StorageMetadataException("The target root has no parent.");
        string name = Path.GetFileName(canonicalTarget);
        return Path.Combine(parent, $".{name}.staging-{migrationId}");
    }

    private static void EnsureOwnedStagingDoesNotExist(string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
        {
            throw new StorageMetadataException("The migration staging path already exists.");
        }
    }

    private static void EnsureDirectoryEmpty(string path)
    {
        if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new StorageMetadataException("The migration target is no longer empty.");
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StorageMetadataException("Storage migration refuses reparse points.");
        }
    }

    private static void DeleteRuntimeDatabaseFiles(string databasePath)
    {
        TryDelete($"{databasePath}-wal");
        TryDelete($"{databasePath}-shm");
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        EnsureNotReparsePoint(directory);
        Directory.Delete(directory, recursive: true);
    }

    private static void QuarantineFailedTarget(string targetRoot, string migrationId)
    {
        if (!Directory.Exists(targetRoot))
        {
            return;
        }

        string failedPath = $"{targetRoot}.failed-{migrationId}";
        if (!Directory.Exists(failedPath) && !File.Exists(failedPath))
        {
            Directory.Move(targetRoot, failedPath);
        }
    }

    private static string ClassifyError(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        UnauthorizedAccessException => "access-denied",
        TimeoutException => "startup-timeout",
        StorageMetadataException => "verification-failed",
        IOException => "io-failed",
        InvalidOperationException => "invalid-state",
        _ => "unexpected-failure",
    };

    private bool IsAncestorOrDescendant(string left, string right) =>
        _platformService.GetPathRelation(left, right) != StoragePathRelation.Unrelated;

    private bool IsSameOrDescendant(string path, string parent) =>
        _platformService.GetPathRelation(path, parent) is
            StoragePathRelation.Same or StoragePathRelation.Descendant;

    private bool PathEquals(string left, string right) =>
        _platformService.GetPathRelation(left, right) == StoragePathRelation.Same;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
