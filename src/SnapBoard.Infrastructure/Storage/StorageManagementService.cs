using SnapBoard.Application.Storage;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Infrastructure.Storage;

public sealed class StorageManagementService : IStorageManagementService
{
    private const long MinimumFreeSpaceMarginBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan MaximumManifestAge = TimeSpan.FromMinutes(10);
    private readonly ResolvedStorageLocation _activeLocation;
    private readonly StorageBootstrapPaths _bootstrapPaths;
    private readonly IReadOnlyList<string> _cloudDirectories;
    private readonly string _installationDirectory;
    private readonly StorageLocationStore _locationStore;
    private readonly IStoragePlatformService _platformService;
    private readonly string _temporaryDirectory;
    private readonly string _userHomeDirectory;

    public StorageManagementService(
        StorageBootstrapPaths bootstrapPaths,
        StorageLocationStore locationStore,
        ResolvedStorageLocation activeLocation,
        IStoragePlatformService platformService,
        string? installationDirectory = null,
        string? temporaryDirectory = null,
        string? userHomeDirectory = null,
        IReadOnlyList<string>? cloudDirectories = null)
    {
        _bootstrapPaths = bootstrapPaths ??
            throw new ArgumentNullException(nameof(bootstrapPaths));
        _locationStore = locationStore ?? throw new ArgumentNullException(nameof(locationStore));
        _activeLocation = activeLocation ?? throw new ArgumentNullException(nameof(activeLocation));
        _platformService = platformService ?? throw new ArgumentNullException(nameof(platformService));
        _installationDirectory = Path.GetFullPath(
            installationDirectory ?? AppContext.BaseDirectory);
        _temporaryDirectory = Path.GetFullPath(temporaryDirectory ?? Path.GetTempPath());
        _userHomeDirectory = Path.GetFullPath(
            userHomeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _cloudDirectories = cloudDirectories ?? GetKnownCloudDirectories(_userHomeDirectory);
    }

    public async ValueTask<StorageLocationSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        StorageUsage usage = await StorageUsageCalculator.CalculateAsync(
                _activeLocation.Paths,
                cancellationToken)
            .ConfigureAwait(false);
        StorageMigrationStateDocument? state = await _locationStore
            .ReadMigrationStateAsync(cancellationToken)
            .ConfigureAwait(false);
        return new StorageLocationSnapshot(
            _activeLocation.Paths.RootDirectory,
            _bootstrapPaths.DefaultDataRoot,
            _activeLocation.Location.StorageInstanceId,
            _activeLocation.Location.VolumeIdentity,
            usage,
            _activeLocation.Location.RollbackDataRoot,
            state?.Phase ?? StorageMigrationPhase.None,
            state?.MigrationId,
            state?.ErrorCode);
    }

    public async ValueTask<StorageLocationValidationResult> ValidateTargetAsync(
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return Invalid(
                string.Empty,
                StorageLocationValidationError.InvalidPath,
                "empty-path");
        }

        string canonicalTarget;
        try
        {
            canonicalTarget = NormalizeDirectoryPath(targetDirectory);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid(
                targetDirectory,
                StorageLocationValidationError.InvalidPath,
                "invalid-path");
        }

        if (!Directory.Exists(canonicalTarget) &&
            IsSamePath(canonicalTarget, _bootstrapPaths.DefaultDataRoot))
        {
            await _platformService.EnsurePrivateDirectoryAsync(
                    canonicalTarget,
                    StorageDirectorySecurityMode.ApplicationOwnedRoot,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!Directory.Exists(canonicalTarget))
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.Unavailable,
                "target-not-found");
        }

        if (IsFileSystemRoot(canonicalTarget) ||
            IsSamePath(canonicalTarget, _userHomeDirectory))
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.PathTooBroad,
                "broad-target");
        }

        if (IsSamePath(canonicalTarget, _activeLocation.Paths.RootDirectory))
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.SameAsCurrent,
                "same-as-current");
        }

        if (IsAncestorOrDescendant(canonicalTarget, _activeLocation.Paths.RootDirectory))
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.NestedWithCurrent,
                "nested-with-current");
        }

        if (IsAncestorOrDescendant(canonicalTarget, _installationDirectory) ||
            IsAncestorOrDescendant(canonicalTarget, _temporaryDirectory) ||
            IsAncestorOrDescendant(canonicalTarget, _bootstrapPaths.BootstrapDirectory) ||
            IsKnownCloudDirectory(canonicalTarget))
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.ReservedLocation,
                "reserved-target");
        }

        StoragePathInspection inspection;
        try
        {
            inspection = await _platformService.InspectPathAsync(
                    canonicalTarget,
                    probeWriteCapabilities: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.ProbeFailed,
                "inspection-failed");
        }

        if (inspection.VolumeKind != StorageVolumeKind.Fixed)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.UnsupportedVolume,
                "volume-not-fixed",
                inspection);
        }

        if (inspection.ContainsReparsePoint)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.ReparsePoint,
                "reparse-point",
                inspection);
        }

        StorageInstanceDocument? existingInstance;
        bool targetHasEntries;
        try
        {
            existingInstance = await _locationStore.ReadInstanceMarkerAsync(
                    canonicalTarget,
                    cancellationToken)
                .ConfigureAwait(false);
            targetHasEntries = Directory.EnumerateFileSystemEntries(canonicalTarget).Any();
        }
        catch (StorageMetadataException)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.ExistingStorage,
                "invalid-instance-marker",
                inspection);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.Unavailable,
                "target-enumeration-failed",
                inspection);
        }

        if (existingInstance is not null || targetHasEntries)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.ExistingStorage,
                existingInstance is null ? "target-not-empty" : "target-has-storage",
                inspection);
        }

        if (!inspection.IsPrivateToCurrentUser)
        {
            try
            {
                // 用户明确选择的空目录可以安全收紧 ACL；非空目录在上方已拒绝，
                // 避免验证操作改变其他文件的访问权限。
                await _platformService.EnsurePrivateDirectoryAsync(
                        canonicalTarget,
                        StorageDirectorySecurityMode.EmptyDirectoryOnly,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Invalid(
                    canonicalTarget,
                    StorageLocationValidationError.InsecurePermissions,
                    "acl-hardening-failed",
                    inspection);
            }
        }

        try
        {
            inspection = await _platformService.InspectPathAsync(
                    canonicalTarget,
                    probeWriteCapabilities: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.ProbeFailed,
                "inspection-after-hardening-failed");
        }

        if (inspection.VolumeKind != StorageVolumeKind.Fixed)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.UnsupportedVolume,
                "volume-changed",
                inspection);
        }

        if (inspection.ContainsReparsePoint)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.ReparsePoint,
                "reparse-point-after-hardening",
                inspection);
        }

        if (!inspection.IsPrivateToCurrentUser)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.InsecurePermissions,
                "insecure-acl",
                inspection);
        }

        if (!inspection.SupportsWriteThroughAndAtomicRename)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.ProbeFailed,
                "write-probe-failed",
                inspection);
        }

        StorageUsage usage;
        try
        {
            usage = await StorageUsageCalculator.CalculateAsync(
                    _activeLocation.Paths,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or OverflowException)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.Unavailable,
                "source-size-failed",
                inspection);
        }

        long margin = Math.Max(MinimumFreeSpaceMarginBytes, usage.TotalBytes / 4);
        long required = checked(usage.TotalBytes + margin);
        if (inspection.AvailableBytes < required)
        {
            return new StorageLocationValidationResult(
                false,
                canonicalTarget,
                inspection.VolumeIdentity,
                inspection.AvailableBytes,
                required,
                StorageLocationValidationError.InsufficientSpace,
                "insufficient-space");
        }

        // 源数据统计可能耗时，返回成功前再次枚举以缩短目录状态变化的竞态窗口。
        try
        {
            if (Directory.EnumerateFileSystemEntries(canonicalTarget).Any())
            {
                return Invalid(
                    canonicalTarget,
                    StorageLocationValidationError.ExistingStorage,
                    "target-not-empty",
                    inspection);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(
                canonicalTarget,
                StorageLocationValidationError.Unavailable,
                "target-final-enumeration-failed",
                inspection);
        }

        return new StorageLocationValidationResult(
            true,
            canonicalTarget,
            inspection.VolumeIdentity,
            inspection.AvailableBytes,
            required,
            StorageLocationValidationError.None);
    }

    public async ValueTask<StorageMigrationLaunchPlan> PrepareMigrationAsync(
        string targetDirectory,
        StorageProcessIdentity mainProcess,
        string mainExecutablePath,
        string migratorExecutablePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mainProcess);
        string canonicalMainExecutable = RequireExistingRegularFile(mainExecutablePath);
        string canonicalMigratorExecutable = RequireExistingRegularFile(migratorExecutablePath);
        if (!IsSamePath(canonicalMainExecutable, mainProcess.ExecutablePath))
        {
            throw new InvalidOperationException("The main process executable identity does not match.");
        }

        StorageMigrationStateDocument? existingState = await _locationStore
            .ReadMigrationStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existingState is not null && existingState.Phase is not
            (StorageMigrationPhase.Completed or
             StorageMigrationPhase.RolledBack or
             StorageMigrationPhase.Failed))
        {
            throw new InvalidOperationException("Another storage migration is already active.");
        }

        StorageLocationValidationResult validation = await ValidateTargetAsync(
                targetDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new StorageLocationValidationException(validation);
        }

        await _platformService.EnsurePrivateDirectoryAsync(
                validation.CanonicalTargetDirectory,
                StorageDirectorySecurityMode.EmptyDirectoryOnly,
                cancellationToken)
            .ConfigureAwait(false);
        string migrationId = $"m-{Guid.NewGuid():N}";
        string manifestPath = _bootstrapPaths.GetManifestPath(migrationId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        StorageMigrationManifest manifest = new(
            StorageDocumentVersions.MigrationManifest,
            migrationId,
            _bootstrapPaths.BootstrapDirectory,
            _activeLocation.Paths.RootDirectory,
            validation.CanonicalTargetDirectory,
            _activeLocation.Location.StorageInstanceId,
            _activeLocation.Location.VolumeIdentity,
            validation.VolumeIdentity,
            validation.RequiredBytes,
            mainProcess,
            canonicalMainExecutable,
            canonicalMigratorExecutable,
            now,
            string.Empty);
        StorageMigrationStateDocument state = new(
            StorageDocumentVersions.MigrationState,
            migrationId,
            StorageMigrationPhase.Requested,
            manifest.SourceDataRoot,
            manifest.TargetDataRoot,
            manifest.StorageInstanceId,
            now,
            now,
            LocatorSwitched: false,
            StartupAcknowledged: false,
            ErrorCode: null,
            Integrity: string.Empty);

        await _locationStore.WriteManifestAsync(manifestPath, manifest, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _locationStore.WriteMigrationStateAsync(state, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDelete(manifestPath);
            throw;
        }

        return new StorageMigrationLaunchPlan(
            migrationId,
            manifestPath,
            canonicalMigratorExecutable,
            ["--manifest", manifestPath]);
    }

    public async ValueTask AcknowledgeStartupAsync(
        string migrationId,
        StorageProcessIdentity process,
        CancellationToken cancellationToken)
    {
        StorageLocationStore.ValidateIdentifier(migrationId, nameof(migrationId));
        ArgumentNullException.ThrowIfNull(process);
        StorageMigrationStateDocument state = await _locationStore
            .ReadMigrationStateAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new StorageMetadataException(
                "The migration state is missing during startup acknowledgement.");
        if (!string.Equals(state.MigrationId, migrationId, StringComparison.Ordinal) ||
            !string.Equals(
                state.StorageInstanceId,
                _activeLocation.Location.StorageInstanceId,
                StringComparison.Ordinal) ||
            !IsSamePath(state.TargetDataRoot, _activeLocation.Paths.RootDirectory) ||
            !state.LocatorSwitched ||
            state.Phase != StorageMigrationPhase.WaitingForStartupAcknowledgement)
        {
            throw new StorageMetadataException("The startup acknowledgement does not match migration state.");
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _locationStore.WriteStartupAcknowledgementAsync(
                new StorageStartupAcknowledgementDocument(
                    StorageDocumentVersions.StartupAcknowledgement,
                    migrationId,
                    state.StorageInstanceId,
                    process,
                    now,
                    string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        await _locationStore.WriteMigrationStateAsync(
                state with
                {
                    StartupAcknowledged = true,
                    UpdatedAtUtc = now,
                    Integrity = string.Empty,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask CancelPreparedMigrationAsync(
        string migrationId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        StorageLocationStore.ValidateIdentifier(migrationId, nameof(migrationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        StorageMigrationStateDocument state = await _locationStore
            .ReadMigrationStateAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw new StorageMetadataException(
                "The migration state is missing during cancellation.");
        if (!string.Equals(state.MigrationId, migrationId, StringComparison.Ordinal) ||
            state.LocatorSwitched ||
            state.Phase is StorageMigrationPhase.Completed or StorageMigrationPhase.RolledBack)
        {
            throw new StorageMetadataException("The prepared migration cannot be cancelled.");
        }

        await _locationStore.WriteMigrationStateAsync(
                state with
                {
                    Phase = StorageMigrationPhase.RolledBack,
                    UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ErrorCode = errorCode,
                    Integrity = string.Empty,
                },
                cancellationToken)
            .ConfigureAwait(false);
        TryDelete(_bootstrapPaths.GetManifestPath(migrationId));
        TryDelete(_bootstrapPaths.GetStartupAcknowledgementPath(migrationId));
    }

    internal static bool IsManifestFresh(StorageMigrationManifest manifest)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset createdAt = DateTimeOffset.FromUnixTimeMilliseconds(manifest.CreatedAtUtc);
        return createdAt <= now + TimeSpan.FromMinutes(1) &&
            now - createdAt <= MaximumManifestAge;
    }

    private static StorageLocationValidationResult Invalid(
        string target,
        StorageLocationValidationError error,
        string code,
        StoragePathInspection? inspection = null) => new(
        false,
        target,
        inspection?.VolumeIdentity ?? string.Empty,
        inspection?.AvailableBytes ?? 0,
        RequiredBytes: 0,
        error,
        code);

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private bool IsFileSystemRoot(string path) =>
        IsSamePath(path, Path.GetPathRoot(path) ?? path);

    private bool IsAncestorOrDescendant(string left, string right) =>
        _platformService.GetPathRelation(left, right) != StoragePathRelation.Unrelated;

    private bool IsSameOrDescendant(string path, string parent) =>
        _platformService.GetPathRelation(path, parent) is
            StoragePathRelation.Same or StoragePathRelation.Descendant;

    private bool IsSamePath(string left, string right) =>
        _platformService.GetPathRelation(left, right) == StoragePathRelation.Same;

    private bool IsKnownCloudDirectory(string target)
    {
        foreach (string directory in _cloudDirectories)
        {
            if (IsAncestorOrDescendant(target, directory))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> GetKnownCloudDirectories(string userHomeDirectory)
    {
        List<string> directories = [];
        foreach (string variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                directories.Add(Path.GetFullPath(value));
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            directories.Add(Path.Combine(userHomeDirectory, "Library", "Mobile Documents"));
            directories.Add(Path.Combine(userHomeDirectory, "Library", "CloudStorage"));
        }

        return directories;
    }

    private static string RequireExistingRegularFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string canonicalPath = Path.GetFullPath(path);
        if (!File.Exists(canonicalPath) ||
            (File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("A required executable is unavailable.", canonicalPath);
        }

        return canonicalPath;
    }

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
