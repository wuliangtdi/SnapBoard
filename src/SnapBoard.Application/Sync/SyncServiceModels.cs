namespace SnapBoard.Application.Sync;

public enum SyncServiceState
{
    NotConfigured = 0,
    Disabled = 1,
    Idle = 2,
    Synchronizing = 3,
    Paused = 4,
    AuthenticationRequired = 5,
    PermissionDenied = 6,
    KeyUnavailable = 7,
    Error = 8,
}

public sealed record SyncStatusSnapshot(
    SyncServiceState State,
    Guid? SpaceId = null,
    DateTimeOffset? LastSuccessfulSync = null,
    int UploadedEvents = 0,
    int DownloadedEvents = 0,
    string? DiagnosticCode = null);

public enum SyncSetupStatus
{
    Success = 0,
    InvalidConfiguration = 1,
    CredentialStoreFailed = 2,
    KeyStoreFailed = 3,
    RecoveryMaterialFailed = 4,
    AuthenticationFailed = 5,
    PermissionDenied = 6,
    RemoteUnavailable = 7,
    RemoteProtocolError = 8,
    CryptographicFailure = 9,
    PersistenceFailure = 10,
}

public sealed record SyncSetupResult(
    SyncSetupStatus Status,
    Guid? SpaceId = null,
    Guid? DeviceId = null,
    string? RecoveryMaterialPath = null,
    string? DiagnosticCode = null);

public sealed record SyncSetupRequest(SyncRemoteConfiguration RemoteConfiguration);

public interface ISyncService
{
    event EventHandler<SyncStatusSnapshot>? StatusChanged;

    event EventHandler<SyncPollingSettingsChangedEvent>? PollingSettingsChanged;

    SyncStatusSnapshot Status { get; }

    SyncPollingSettings PollingSettings { get; }

    void Start();

    bool RequestSync();

    ValueTask InitializePollingSettingsAsync(CancellationToken cancellationToken);

    ValueTask UpdatePollingSettingsAsync(
        SyncPollingSettings settings,
        CancellationToken cancellationToken);

    ValueTask<SyncStatusSnapshot> SynchronizeNowAsync(CancellationToken cancellationToken);

    ValueTask<SyncSetupResult> CreateSpaceAsync(
        SyncSetupRequest request,
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken);

    ValueTask<SyncSetupResult> JoinSpaceAsync(
        Guid spaceId,
        int keyVersion,
        SyncSetupRequest request,
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> recoveryEnvelope,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken);

    ValueTask PauseAndDrainAsync(CancellationToken cancellationToken);

    void ResumeAfterPause();
}
