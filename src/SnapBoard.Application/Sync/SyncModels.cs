using System.Security.Cryptography;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Application.Sync;

public sealed record SyncConfigurationSnapshot(
    Guid SpaceId,
    Guid DeviceId,
    int KeyVersion,
    bool IsEnabled,
    long NextSequence);

public sealed record SyncOutboxItem(
    SyncEventEnvelope Event,
    byte[] SerializedEvent,
    int RetryCount);

public enum SyncPersistenceErrorCategory
{
    None = 0,
    Authentication = 1,
    Permission = 2,
    Conflict = 3,
    RateLimited = 4,
    Transient = 5,
    Network = 6,
    Protocol = 7,
    Cryptographic = 8,
}

public sealed record SyncCheckpointState(
    Guid SpaceId,
    Guid DeviceId,
    long AppliedSequence,
    Guid? AppliedEventId,
    string? ETag);

public enum SyncEventApplyStatus
{
    Applied = 0,
    Duplicate = 1,
    SequenceGap = 2,
    ConflictIgnored = 3,
}

public sealed record SyncEventApplyResult(
    SyncEventApplyStatus Status,
    long ExpectedSequence);

public sealed record SyncProviderMigrationRecord(
    Guid PlanId,
    Guid SpaceId,
    long Epoch,
    Guid InitiatorDeviceId,
    string SourceRemoteFingerprint,
    string TargetRemoteFingerprint,
    SyncProviderMigrationState State,
    int TotalObjects,
    long TotalBytes,
    int CompletedObjects,
    long CompletedBytes,
    string? InventorySha256,
    string? DiagnosticCode,
    long CreatedAtUnixMilliseconds,
    long UpdatedAtUnixMilliseconds);

public sealed record SyncProviderMigrationDeviceRecord(
    Guid PlanId,
    Guid DeviceId,
    SyncProviderMigrationDeviceState State,
    long HighestLocalSequence,
    long HighestUploadedSequence,
    string? DiagnosticCode,
    long UpdatedAtUnixMilliseconds);

public sealed record SyncProviderMigrationWatermark(
    long HighestLocalSequence,
    long HighestUploadedSequence,
    IReadOnlyList<SyncCheckpointState> Checkpoints);

public sealed class SyncBlobLease : IDisposable
{
    private byte[]? _content;

    public SyncBlobLease(string hash, string mediaType, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(content);
        Hash = hash;
        MediaType = mediaType;
        _content = content;
    }

    public string Hash { get; }

    public string MediaType { get; }

    public ReadOnlyMemory<byte> Content => _content ??
        throw new ObjectDisposedException(nameof(SyncBlobLease));

    public void Dispose()
    {
        byte[]? content = Interlocked.Exchange(ref _content, null);
        if (content is not null)
        {
            CryptographicOperations.ZeroMemory(content);
        }

        GC.SuppressFinalize(this);
    }
}

public interface ISyncStore
{
    ValueTask ConfigureAsync(
        Guid spaceId,
        Guid deviceId,
        int keyVersion,
        bool enabled,
        CancellationToken cancellationToken);

    ValueTask<SyncConfigurationSnapshot?> GetConfigurationAsync(
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SyncOutboxItem>> ReadOutboxBatchAsync(
        int maximumCount,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask MarkOutboxUploadedAsync(
        Guid eventId,
        string? remoteEtag,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken);

    ValueTask MarkOutboxFailedAsync(
        Guid eventId,
        SyncPersistenceErrorCategory errorCategory,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken);

    ValueTask<SyncCheckpointState> GetCheckpointAsync(
        Guid spaceId,
        Guid remoteDeviceId,
        CancellationToken cancellationToken);

    ValueTask EnsureRemoteDeviceAsync(
        Guid spaceId,
        Guid remoteDeviceId,
        CancellationToken cancellationToken);

    ValueTask<SyncEventApplyResult> ApplyRemoteEventAsync(
        SyncEventEnvelope syncEvent,
        ReadOnlyMemory<byte> serializedEvent,
        string? remoteEtag,
        CancellationToken cancellationToken);

    ValueTask<SyncBlobLease?> OpenBlobAsync(
        string plaintextHash,
        CancellationToken cancellationToken);

    ValueTask<bool> ContainsBlobAsync(
        string plaintextHash,
        string mediaType,
        long sizeBytes,
        CancellationToken cancellationToken);

    ValueTask StageDownloadedBlobAsync(
        string plaintextHash,
        string mediaType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    ValueTask<SyncProviderMigrationRecord?> GetProviderMigrationAsync(
        Guid spaceId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SyncProviderMigrationDeviceRecord>>
        GetProviderMigrationDevicesAsync(
            Guid planId,
            CancellationToken cancellationToken);

    ValueTask CreateProviderMigrationAsync(
        SyncProviderMigrationRecord migration,
        IReadOnlyList<Guid> requiredDeviceIds,
        CancellationToken cancellationToken);

    ValueTask SaveProviderMigrationAsync(
        SyncProviderMigrationRecord migration,
        CancellationToken cancellationToken);

    ValueTask SaveProviderMigrationDeviceAsync(
        SyncProviderMigrationDeviceRecord device,
        CancellationToken cancellationToken);

    ValueTask<SyncProviderMigrationWatermark> CaptureProviderMigrationWatermarkAsync(
        Guid spaceId,
        Guid localDeviceId,
        CancellationToken cancellationToken);
}
