namespace SnapBoard.Application.Storage;

public enum StorageMigrationPhase
{
    None = 0,
    Requested = 1,
    WaitingForMainProcessExit = 2,
    ValidatingSource = 3,
    CopyingToStaging = 4,
    VerifyingDestination = 5,
    SwitchingLocation = 6,
    StartingMainApplication = 7,
    WaitingForStartupAcknowledgement = 8,
    Completed = 9,
    RollingBack = 10,
    RolledBack = 11,
    Failed = 12,
}

public enum StorageLocationValidationError
{
    None = 0,
    InvalidPath = 1,
    PathTooBroad = 2,
    SameAsCurrent = 3,
    NestedWithCurrent = 4,
    ReservedLocation = 5,
    UnsupportedVolume = 6,
    ReparsePoint = 7,
    InsufficientSpace = 8,
    InsecurePermissions = 9,
    ExistingStorage = 10,
    ProbeFailed = 11,
    Unavailable = 12,
}

public sealed record StorageUsage(
    long DatabaseBytes,
    long BlobBytes,
    long RecoveryBytes)
{
    public long TotalBytes => checked(DatabaseBytes + BlobBytes + RecoveryBytes);
}

public sealed record StorageLocationSnapshot(
    string RootDirectory,
    string DefaultRootDirectory,
    string StorageInstanceId,
    string VolumeIdentity,
    StorageUsage Usage,
    string? RollbackDirectory,
    StorageMigrationPhase MigrationPhase,
    string? MigrationId,
    string? LastErrorCode);

public sealed record StorageLocationValidationResult(
    bool IsValid,
    string CanonicalTargetDirectory,
    string VolumeIdentity,
    long AvailableBytes,
    long RequiredBytes,
    StorageLocationValidationError Error,
    string? ErrorCode = null);

public sealed class StorageLocationValidationException : InvalidOperationException
{
    public StorageLocationValidationException(StorageLocationValidationResult validation)
        : base("The storage target failed final validation.")
    {
        ArgumentNullException.ThrowIfNull(validation);
        Validation = validation;
    }

    public StorageLocationValidationResult Validation { get; }
}

public sealed record StorageMigrationLaunchPlan(
    string MigrationId,
    string ManifestPath,
    string MigratorExecutablePath,
    IReadOnlyList<string> Arguments);
