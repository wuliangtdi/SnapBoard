namespace SnapBoard.Application.Sync;

public enum SyncProviderMigrationState
{
    None = 0,
    Draft = 1,
    PreflightTarget = 2,
    PreparingDevices = 3,
    TargetCredentialsRequired = 4,
    WaitingForDeviceAcks = 5,
    Frozen = 6,
    MirroringCiphertext = 7,
    VerifyingTarget = 8,
    Committing = 9,
    WaitingForDeviceCommits = 10,
    Completed = 11,
    RollingBack = 12,
    RolledBack = 13,
    Failed = 14,
}

public enum SyncProviderMigrationDeviceState
{
    Pending = 0,
    TargetCredentialsRequired = 1,
    Ready = 2,
    Committed = 3,
    RolledBack = 4,
    Failed = 5,
}

public enum SyncProviderMigrationStatus
{
    Success = 0,
    NotConfigured = 1,
    NotSupported = 2,
    InvalidState = 3,
    CredentialStoreFailed = 4,
    PermissionDenied = 5,
    RemoteUnavailable = 6,
    RemoteProtocolError = 7,
    CryptographicFailure = 8,
    PersistenceFailure = 9,
    WaitingForDevices = 10,
}

public sealed record SyncProviderMigrationDeviceSnapshot(
    Guid DeviceId,
    SyncProviderMigrationDeviceState State,
    long HighestLocalSequence,
    long HighestUploadedSequence,
    string? DiagnosticCode = null);

public sealed record SyncProviderMigrationSnapshot(
    SyncProviderMigrationState State,
    Guid? PlanId = null,
    Guid? SpaceId = null,
    long Epoch = 0,
    Guid? InitiatorDeviceId = null,
    string? SourceEndpoint = null,
    string? SourceRemoteRoot = null,
    string? TargetEndpoint = null,
    string? TargetRemoteRoot = null,
    IReadOnlyList<SyncProviderMigrationDeviceSnapshot>? Devices = null,
    int TotalObjects = 0,
    long TotalBytes = 0,
    int CompletedObjects = 0,
    long CompletedBytes = 0,
    bool OldRemoteRetained = false,
    string? DiagnosticCode = null,
    string? SourceCertificateSha256Pin = null,
    bool SourceAllowInsecureLoopback = false,
    string? TargetCertificateSha256Pin = null,
    bool TargetAllowInsecureLoopback = false)
{
    public IReadOnlyList<SyncProviderMigrationDeviceSnapshot> DeviceStates => Devices ?? [];
}

public sealed record SyncProviderMigrationRequest(
    SyncRemoteConfiguration TargetConfiguration);

public sealed record SyncProviderMigrationResult(
    SyncProviderMigrationStatus Status,
    SyncProviderMigrationSnapshot Snapshot,
    string? DiagnosticCode = null);

public interface ISyncProviderMigrationService
{
    event EventHandler<SyncProviderMigrationSnapshot>? ProviderMigrationChanged;

    SyncProviderMigrationSnapshot ProviderMigration { get; }

    ValueTask<SyncProviderMigrationSnapshot> RefreshProviderMigrationAsync(
        CancellationToken cancellationToken);

    ValueTask<SyncProviderMigrationResult> StartProviderMigrationAsync(
        SyncProviderMigrationRequest request,
        ReadOnlyMemory<byte> targetPassword,
        CancellationToken cancellationToken);

    ValueTask<SyncProviderMigrationResult> ProvideProviderMigrationCredentialsAsync(
        Guid planId,
        SyncProviderMigrationRequest request,
        ReadOnlyMemory<byte> targetPassword,
        CancellationToken cancellationToken);

    ValueTask<SyncProviderMigrationResult> ContinueProviderMigrationAsync(
        Guid planId,
        CancellationToken cancellationToken);

    ValueTask<SyncProviderMigrationResult> CancelOrRollbackProviderMigrationAsync(
        Guid planId,
        CancellationToken cancellationToken);
}
