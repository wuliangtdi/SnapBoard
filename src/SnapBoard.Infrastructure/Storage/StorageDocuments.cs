using SnapBoard.Application.Storage;
using SnapBoard.Infrastructure.Persistence;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Infrastructure.Storage;

public static class StorageDocumentVersions
{
    public const int Location = 1;
    public const int Instance = 1;
    public const int MigrationManifest = 1;
    public const int MigrationState = 1;
    public const int StartupAcknowledgement = 1;
}

public sealed record StorageLocationDocument(
    int FormatVersion,
    string CurrentDataRoot,
    string StorageInstanceId,
    string VolumeIdentity,
    string? LastMigrationId,
    long? LastMigrationCompletedAtUtc,
    string? RollbackDataRoot,
    string Integrity);

public sealed record StorageInstanceDocument(
    int FormatVersion,
    string StorageInstanceId,
    long CreatedAtUtc,
    string Integrity);

public sealed record StorageMigrationManifest(
    int FormatVersion,
    string MigrationId,
    string BootstrapDirectory,
    string SourceDataRoot,
    string TargetDataRoot,
    string StorageInstanceId,
    string SourceVolumeIdentity,
    string TargetVolumeIdentity,
    long RequiredBytes,
    StorageProcessIdentity MainProcess,
    string MainExecutablePath,
    string MigratorExecutablePath,
    long CreatedAtUtc,
    string Integrity);

public sealed record StorageMigrationStateDocument(
    int FormatVersion,
    string MigrationId,
    StorageMigrationPhase Phase,
    string SourceDataRoot,
    string TargetDataRoot,
    string StorageInstanceId,
    long StartedAtUtc,
    long UpdatedAtUtc,
    bool LocatorSwitched,
    bool StartupAcknowledged,
    string? ErrorCode,
    string Integrity);

public sealed record StorageStartupAcknowledgementDocument(
    int FormatVersion,
    string MigrationId,
    string StorageInstanceId,
    StorageProcessIdentity Process,
    long AcknowledgedAtUtc,
    string Integrity);

public sealed record ResolvedStorageLocation(
    SnapBoardStoragePaths Paths,
    StorageLocationDocument Location);
