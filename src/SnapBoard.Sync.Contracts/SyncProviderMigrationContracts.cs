namespace SnapBoard.Sync.Contracts;

public static class SyncProviderMigrationProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumDevices = 1024;
}

public enum SyncProviderMigrationMarkerKind
{
    Intent = 1,
    Ready = 2,
    Freeze = 3,
    Commit = 4,
    Committed = 5,
    Rollback = 6,
    RolledBack = 7,
    Completed = 8,
}

public sealed record SyncProviderMigrationCheckpoint(
    Guid DeviceId,
    long AppliedSequence,
    Guid? AppliedEventId,
    string? ETag);

public sealed record SyncProviderMigrationIntent(
    int MigrationProtocolVersion,
    Guid PlanId,
    Guid SpaceId,
    long Epoch,
    Guid InitiatorDeviceId,
    string SourceEndpoint,
    string SourceRemoteRoot,
    string? SourceCertificateSha256Pin,
    bool SourceAllowInsecureLoopback,
    string SourceRemoteFingerprint,
    string TargetEndpoint,
    string TargetRemoteRoot,
    string? TargetCertificateSha256Pin,
    bool TargetAllowInsecureLoopback,
    string TargetRemoteFingerprint,
    Guid[] RequiredDeviceIds,
    long CreatedAtUnixMilliseconds);

public sealed record SyncProviderMigrationDeviceMarker(
    int MigrationProtocolVersion,
    SyncProviderMigrationMarkerKind Kind,
    Guid PlanId,
    Guid SpaceId,
    long Epoch,
    Guid DeviceId,
    long HighestLocalSequence,
    long HighestUploadedSequence,
    SyncProviderMigrationCheckpoint[] Checkpoints,
    long CreatedAtUnixMilliseconds,
    string? DiagnosticCode = null);

public sealed record SyncProviderMigrationDecision(
    int MigrationProtocolVersion,
    SyncProviderMigrationMarkerKind Kind,
    Guid PlanId,
    Guid SpaceId,
    long Epoch,
    Guid InitiatorDeviceId,
    int ObjectCount,
    long TotalBytes,
    string? InventorySha256,
    long CreatedAtUnixMilliseconds,
    string? DiagnosticCode = null);
