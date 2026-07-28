using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Application.Storage;

public interface IStorageManagementService
{
    ValueTask<StorageLocationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    ValueTask<StorageLocationValidationResult> ValidateTargetAsync(
        string targetDirectory,
        CancellationToken cancellationToken);

    ValueTask<StorageMigrationLaunchPlan> PrepareMigrationAsync(
        string targetDirectory,
        StorageProcessIdentity mainProcess,
        string mainExecutablePath,
        string migratorExecutablePath,
        CancellationToken cancellationToken);

    ValueTask AcknowledgeStartupAsync(
        string migrationId,
        StorageProcessIdentity process,
        CancellationToken cancellationToken);

    ValueTask CancelPreparedMigrationAsync(
        string migrationId,
        string errorCode,
        CancellationToken cancellationToken);
}

public interface IStorageMigrationBarrier
{
    ValueTask PrepareForMigrationAsync(CancellationToken cancellationToken);
}
