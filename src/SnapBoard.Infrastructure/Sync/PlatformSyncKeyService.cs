using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SnapBoard.Application.Sync;
using SnapBoard.Platform.Abstractions.Security;

namespace SnapBoard.Infrastructure.Sync;

public sealed class PlatformSyncKeyService : ISyncKeyService
{
    private readonly SyncRecoveryKdfParameters _recoveryParameters;
    private readonly IPlatformSecretStore _secretStore;

    public PlatformSyncKeyService(IPlatformSecretStore secretStore)
        : this(secretStore, new SyncRecoveryKdfParameters())
    {
    }

    internal PlatformSyncKeyService(
        IPlatformSecretStore secretStore,
        SyncRecoveryKdfParameters recoveryParameters)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _recoveryParameters = recoveryParameters ??
            throw new ArgumentNullException(nameof(recoveryParameters));
    }

    public async ValueTask<SyncSpaceKeyCreationResult> CreateSpaceKeyAsync(
        Guid spaceId,
        int keyVersion,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken)
    {
        string secretName = GetSecretName(spaceId, keyVersion);
        byte[] masterKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            byte[] recoveryEnvelope = await SyncRecoveryKeyProtector.WrapAsync(
                    masterKey,
                    recoveryCode,
                    cancellationToken,
                    _recoveryParameters)
                .ConfigureAwait(false);
            PlatformSecretWriteResult write = await _secretStore.WriteAsync(
                    secretName,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncKeyOperationStatus status = MapStatus(write.Status);
            if (status != SyncKeyOperationStatus.Success)
            {
                CryptographicOperations.ZeroMemory(recoveryEnvelope);
                return new SyncSpaceKeyCreationResult(status);
            }

            return new SyncSpaceKeyCreationResult(status, recoveryEnvelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public async ValueTask<SyncKeyOperationStatus> ImportSpaceKeyAsync(
        Guid spaceId,
        int keyVersion,
        ReadOnlyMemory<byte> recoveryEnvelope,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken)
    {
        SyncMasterKeyOpenResult recovered = await RecoverMasterKeyAsync(
                recoveryEnvelope,
                recoveryCode,
                cancellationToken)
            .ConfigureAwait(false);
        if (recovered.Status != SyncKeyOperationStatus.Success || recovered.Key is null)
        {
            return recovered.Status;
        }

        using (recovered.Key)
        {
            PlatformSecretWriteResult result = await _secretStore.WriteAsync(
                    GetSecretName(spaceId, keyVersion),
                    recovered.Key.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(result.Status);
        }
    }

    public async ValueTask<SyncMasterKeyOpenResult> RecoverMasterKeyAsync(
        ReadOnlyMemory<byte> recoveryEnvelope,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken)
    {
        byte[] masterKey = await SyncRecoveryKeyProtector.UnwrapAsync(
                recoveryEnvelope,
                recoveryCode,
                cancellationToken)
            .ConfigureAwait(false);
        if (masterKey.Length != 32)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            return new SyncMasterKeyOpenResult(SyncKeyOperationStatus.Failed);
        }

        return new SyncMasterKeyOpenResult(
            SyncKeyOperationStatus.Success,
            new SyncMasterKeyLease(masterKey));
    }

    public async ValueTask<SyncMasterKeyOpenResult> OpenMasterKeyAsync(
        Guid spaceId,
        int keyVersion,
        CancellationToken cancellationToken)
    {
        PlatformSecretReadResult result = await _secretStore.ReadAsync(
                GetSecretName(spaceId, keyVersion),
                cancellationToken)
            .ConfigureAwait(false);
        SyncKeyOperationStatus status = MapStatus(result.Status);
        if (status != SyncKeyOperationStatus.Success || result.Secret.Length != 32)
        {
            if (!result.Secret.IsEmpty)
            {
                ZeroOwnedSecret(result.Secret);
            }

            return new SyncMasterKeyOpenResult(
                status == SyncKeyOperationStatus.Success
                    ? SyncKeyOperationStatus.Failed
                    : status);
        }

        byte[] ownedKey = result.Secret.ToArray();
        ZeroOwnedSecret(result.Secret);
        return new SyncMasterKeyOpenResult(
            SyncKeyOperationStatus.Success,
            new SyncMasterKeyLease(ownedKey));
    }

    public async ValueTask<SyncKeyOperationStatus> DeleteSpaceKeyAsync(
        Guid spaceId,
        int keyVersion,
        CancellationToken cancellationToken)
    {
        PlatformSecretWriteResult result = await _secretStore.DeleteAsync(
                GetSecretName(spaceId, keyVersion),
                cancellationToken)
            .ConfigureAwait(false);
        return MapStatus(result.Status);
    }

    private static string GetSecretName(Guid spaceId, int keyVersion)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(spaceId, Guid.Empty);
        if (keyVersion is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(keyVersion));
        }

        return $"sync/master/{spaceId:N}/v{keyVersion}";
    }

    private static SyncKeyOperationStatus MapStatus(PlatformSecretStoreStatus status) =>
        status switch
        {
            PlatformSecretStoreStatus.Success => SyncKeyOperationStatus.Success,
            PlatformSecretStoreStatus.NotFound => SyncKeyOperationStatus.NotFound,
            PlatformSecretStoreStatus.AccessDenied => SyncKeyOperationStatus.AccessDenied,
            _ => SyncKeyOperationStatus.Failed,
        };

    private static void ZeroOwnedSecret(ReadOnlyMemory<byte> secret)
    {
        if (MemoryMarshal.TryGetArray(secret, out ArraySegment<byte> segment) &&
            segment.Array is not null)
        {
            CryptographicOperations.ZeroMemory(
                segment.Array.AsSpan(segment.Offset, segment.Count));
        }
    }
}
