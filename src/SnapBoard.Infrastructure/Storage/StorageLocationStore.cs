using SnapBoard.Application.Storage;
using SnapBoard.Infrastructure.Persistence;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Infrastructure.Storage;

public sealed class StorageLocationUnavailableException(string message) : IOException(message);

public sealed class StorageLocationStore(
    StorageBootstrapPaths bootstrapPaths,
    IStoragePlatformService platformService) : IAsyncDisposable, IDisposable
{
    private const string InstanceFileName = "storage-instance.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public StorageBootstrapPaths BootstrapPaths { get; } = bootstrapPaths ??
        throw new ArgumentNullException(nameof(bootstrapPaths));

    public async ValueTask<ResolvedStorageLocation> ResolveOrCreateAsync(
        string? startupMigrationId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await platformService.EnsurePrivateDirectoryAsync(
                    BootstrapPaths.BootstrapDirectory,
                    StorageDirectorySecurityMode.ApplicationOwnedRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            await RecoverUnacknowledgedSwitchAsync(startupMigrationId, cancellationToken)
                .ConfigureAwait(false);

            StorageLocationDocument? location = await ReadLocationWithBackupAsync(cancellationToken)
                .ConfigureAwait(false);
            if (location is null)
            {
                location = await CreateInitialLocationAsync(cancellationToken).ConfigureAwait(false);
            }

            ValidateLocationDocument(location);
            string root = Path.GetFullPath(location.CurrentDataRoot);
            if (!Directory.Exists(root))
            {
                throw new StorageLocationUnavailableException(
                    "The configured data directory is unavailable; an empty database was not created.");
            }

            StorageInstanceDocument instance = await ReadAndValidateInstanceAsync(
                    root,
                    location.StorageInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    instance.StorageInstanceId,
                    location.StorageInstanceId,
                    StringComparison.Ordinal))
            {
                throw new StorageMetadataException("The storage instance identifier does not match.");
            }

            return new ResolvedStorageLocation(SnapBoardStoragePaths.Create(root), location);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<StorageLocationDocument?> ReadLocationAsync(
        CancellationToken cancellationToken) => StorageDocumentStore.ReadLocationAsync(
        BootstrapPaths.LocationPath,
        cancellationToken);

    public ValueTask WriteLocationAsync(
        StorageLocationDocument location,
        CancellationToken cancellationToken)
    {
        ValidateLocationDocument(location);
        return StorageDocumentStore.WriteLocationAsync(
            BootstrapPaths.LocationPath,
            location,
            cancellationToken);
    }

    public ValueTask<StorageMigrationStateDocument?> ReadMigrationStateAsync(
        CancellationToken cancellationToken) => StorageDocumentStore.ReadMigrationStateAsync(
        BootstrapPaths.MigrationStatePath,
        cancellationToken);

    public ValueTask WriteMigrationStateAsync(
        StorageMigrationStateDocument state,
        CancellationToken cancellationToken) => StorageDocumentStore.WriteMigrationStateAsync(
        BootstrapPaths.MigrationStatePath,
        state,
        cancellationToken);

    public ValueTask<StorageMigrationManifest?> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        EnsureBootstrapDocumentPath(manifestPath, "migration-", ".json");
        return StorageDocumentStore.ReadManifestAsync(manifestPath, cancellationToken);
    }

    public ValueTask WriteManifestAsync(
        string manifestPath,
        StorageMigrationManifest manifest,
        CancellationToken cancellationToken)
    {
        EnsureBootstrapDocumentPath(manifestPath, "migration-", ".json");
        return StorageDocumentStore.WriteManifestAsync(
            manifestPath,
            manifest,
            cancellationToken);
    }

    public ValueTask<StorageStartupAcknowledgementDocument?> ReadStartupAcknowledgementAsync(
        string migrationId,
        CancellationToken cancellationToken) => StorageDocumentStore.ReadStartupAcknowledgementAsync(
        BootstrapPaths.GetStartupAcknowledgementPath(migrationId),
        cancellationToken);

    public ValueTask WriteStartupAcknowledgementAsync(
        StorageStartupAcknowledgementDocument acknowledgement,
        CancellationToken cancellationToken) => StorageDocumentStore.WriteStartupAcknowledgementAsync(
        BootstrapPaths.GetStartupAcknowledgementPath(acknowledgement.MigrationId),
        acknowledgement,
        cancellationToken);

    public async ValueTask EnsureInstanceMarkerAsync(
        string root,
        string storageInstanceId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateIdentifier(storageInstanceId, nameof(storageInstanceId));
        string path = Path.Combine(Path.GetFullPath(root), InstanceFileName);
        StorageInstanceDocument? existing = await StorageDocumentStore
            .ReadInstanceAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateInstanceDocument(existing);
            if (!string.Equals(
                    existing.StorageInstanceId,
                    storageInstanceId,
                    StringComparison.Ordinal))
            {
                throw new StorageMetadataException(
                    "The target directory belongs to another storage instance.");
            }

            return;
        }

        await StorageDocumentStore.WriteInstanceAsync(
                path,
                new StorageInstanceDocument(
                    StorageDocumentVersions.Instance,
                    storageInstanceId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<StorageInstanceDocument?> ReadInstanceMarkerAsync(
        string root,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        string path = Path.Combine(Path.GetFullPath(root), InstanceFileName);
        return StorageDocumentStore.ReadInstanceAsync(path, cancellationToken);
    }

    private async ValueTask<StorageLocationDocument> CreateInitialLocationAsync(
        CancellationToken cancellationToken)
    {
        string initialRoot = HasLegacyData(BootstrapPaths.LegacyDataRoot)
            ? BootstrapPaths.LegacyDataRoot
            : BootstrapPaths.DefaultDataRoot;
        await platformService.EnsurePrivateDirectoryAsync(
                initialRoot,
                StorageDirectorySecurityMode.ApplicationOwnedRoot,
                cancellationToken)
            .ConfigureAwait(false);
        StoragePathInspection inspection = await platformService.InspectPathAsync(
                initialRoot,
                probeWriteCapabilities: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (inspection.VolumeKind != StorageVolumeKind.Fixed ||
            inspection.ContainsReparsePoint ||
            !inspection.IsPrivateToCurrentUser ||
            !inspection.SupportsWriteThroughAndAtomicRename)
        {
            throw new StorageLocationUnavailableException(
                "The default data directory does not satisfy the storage safety requirements.");
        }

        string instanceId = Guid.NewGuid().ToString("N");
        await EnsureInstanceMarkerAsync(initialRoot, instanceId, cancellationToken)
            .ConfigureAwait(false);
        StorageLocationDocument location = new(
            StorageDocumentVersions.Location,
            initialRoot,
            instanceId,
            inspection.VolumeIdentity,
            LastMigrationId: null,
            LastMigrationCompletedAtUtc: null,
            RollbackDataRoot: null,
            Integrity: string.Empty);
        await WriteLocationAsync(location, cancellationToken).ConfigureAwait(false);
        return (await ReadLocationAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async ValueTask<StorageLocationDocument?> ReadLocationWithBackupAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadLocationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (StorageMetadataException) when (File.Exists($"{BootstrapPaths.LocationPath}.bak"))
        {
            StorageLocationDocument? backup = await StorageDocumentStore.ReadLocationAsync(
                    $"{BootstrapPaths.LocationPath}.bak",
                    cancellationToken)
                .ConfigureAwait(false);
            if (backup is null)
            {
                throw;
            }

            ValidateLocationDocument(backup);
            await WriteLocationAsync(backup, cancellationToken).ConfigureAwait(false);
            return backup;
        }
    }

    private async ValueTask RecoverUnacknowledgedSwitchAsync(
        string? startupMigrationId,
        CancellationToken cancellationToken)
    {
        StorageMigrationStateDocument? state = await ReadMigrationStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state is null || !state.LocatorSwitched || state.StartupAcknowledged ||
            state.Phase is StorageMigrationPhase.Completed or StorageMigrationPhase.RolledBack)
        {
            return;
        }

        if (string.Equals(state.MigrationId, startupMigrationId, StringComparison.Ordinal))
        {
            return;
        }

        StorageLocationDocument? current = await ReadLocationWithBackupAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null || !Directory.Exists(state.SourceDataRoot))
        {
            throw new StorageLocationUnavailableException(
                "An interrupted storage migration requires recovery.");
        }

        StoragePathInspection source = await platformService.InspectPathAsync(
                state.SourceDataRoot,
                probeWriteCapabilities: false,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteLocationAsync(
                current with
                {
                    CurrentDataRoot = Path.GetFullPath(state.SourceDataRoot),
                    VolumeIdentity = source.VolumeIdentity,
                    RollbackDataRoot = state.TargetDataRoot,
                    Integrity = string.Empty,
                },
                cancellationToken)
            .ConfigureAwait(false);
        await WriteMigrationStateAsync(
                state with
                {
                    Phase = StorageMigrationPhase.RolledBack,
                    UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LocatorSwitched = false,
                    ErrorCode = "startup-without-matching-migration-id",
                    Integrity = string.Empty,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<StorageInstanceDocument> ReadAndValidateInstanceAsync(
        string root,
        string expectedInstanceId,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(root, InstanceFileName);
        StorageInstanceDocument? instance = await StorageDocumentStore
            .ReadInstanceAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (instance is null)
        {
            if (PathEquals(root, BootstrapPaths.LegacyDataRoot) && HasLegacyData(root))
            {
                await EnsureInstanceMarkerAsync(root, expectedInstanceId, cancellationToken)
                    .ConfigureAwait(false);
                instance = await StorageDocumentStore.ReadInstanceAsync(path, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new StorageMetadataException("The storage instance marker is missing.");
            }
        }

        ValidateInstanceDocument(instance!);
        return instance!;
    }

    private static void ValidateLocationDocument(StorageLocationDocument document)
    {
        if (document.FormatVersion != StorageDocumentVersions.Location)
        {
            throw new StorageMetadataException("The storage location version is unsupported.");
        }

        ValidateIdentifier(document.StorageInstanceId, nameof(document.StorageInstanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(document.CurrentDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.VolumeIdentity);
        _ = Path.GetFullPath(document.CurrentDataRoot);
    }

    private static void ValidateInstanceDocument(StorageInstanceDocument document)
    {
        if (document.FormatVersion != StorageDocumentVersions.Instance)
        {
            throw new StorageMetadataException("The storage instance version is unsupported.");
        }

        ValidateIdentifier(document.StorageInstanceId, nameof(document.StorageInstanceId));
    }

    internal static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length is < 16 or > 64 || !value.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new StorageMetadataException("A storage protocol identifier is invalid.");
        }
    }

    private static bool HasLegacyData(string root) =>
        File.Exists(Path.Combine(root, "snapboard.db")) ||
        Directory.Exists(Path.Combine(root, "blobs"));

    private bool PathEquals(string left, string right) =>
        platformService.GetPathRelation(left, right) == StoragePathRelation.Same;

    private void EnsureBootstrapDocumentPath(
        string path,
        string requiredPrefix,
        string requiredSuffix)
    {
        string canonicalPath = Path.GetFullPath(path);
        string fileName = Path.GetFileName(canonicalPath);
        if (platformService.GetPathRelation(
                canonicalPath,
                BootstrapPaths.BootstrapDirectory) != StoragePathRelation.Descendant ||
            !fileName.StartsWith(requiredPrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(requiredSuffix, StringComparison.Ordinal))
        {
            throw new StorageMetadataException("The storage document path is outside bootstrap.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
