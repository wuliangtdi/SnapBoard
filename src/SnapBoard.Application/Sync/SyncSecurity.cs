using System.Security.Cryptography;

namespace SnapBoard.Application.Sync;

public enum SyncKeyOperationStatus
{
    Success = 0,
    NotFound = 1,
    AccessDenied = 2,
    Failed = 3,
}

public sealed class SyncMasterKeyLease : IDisposable
{
    private byte[]? _key;

    public SyncMasterKeyLease(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("A sync master key must be 256 bits.", nameof(key));
        }

        _key = key;
    }

    public ReadOnlyMemory<byte> Key => _key ??
        throw new ObjectDisposedException(nameof(SyncMasterKeyLease));

    public void Dispose()
    {
        byte[]? key = Interlocked.Exchange(ref _key, null);
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        GC.SuppressFinalize(this);
    }
}

public sealed record SyncSpaceKeyCreationResult(
    SyncKeyOperationStatus Status,
    byte[]? RecoveryEnvelope = null);

public sealed record SyncMasterKeyOpenResult(
    SyncKeyOperationStatus Status,
    SyncMasterKeyLease? Key = null);

public interface ISyncKeyService
{
    ValueTask<SyncSpaceKeyCreationResult> CreateSpaceKeyAsync(
        Guid spaceId,
        int keyVersion,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken);

    ValueTask<SyncKeyOperationStatus> ImportSpaceKeyAsync(
        Guid spaceId,
        int keyVersion,
        ReadOnlyMemory<byte> recoveryEnvelope,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken);

    ValueTask<SyncMasterKeyOpenResult> OpenMasterKeyAsync(
        Guid spaceId,
        int keyVersion,
        CancellationToken cancellationToken);

    ValueTask<SyncKeyOperationStatus> DeleteSpaceKeyAsync(
        Guid spaceId,
        int keyVersion,
        CancellationToken cancellationToken);
}
