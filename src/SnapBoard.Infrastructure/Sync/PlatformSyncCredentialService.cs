using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SnapBoard.Application.Sync;
using SnapBoard.Platform.Abstractions.Security;

namespace SnapBoard.Infrastructure.Sync;

public sealed class PlatformSyncCredentialService : ISyncCredentialService, IDisposable
{
    private const byte BundleVersion = 1;
    private const byte InsecureLoopbackFlag = 1;
    private const int HeaderLength = 16;
    private const int MaximumBundleBytes = 5 * 512;
    private const int MaximumPasswordBytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IPlatformSecretStore _secretStore;
    private int _disposed;

    public PlatformSyncCredentialService(IPlatformSecretStore secretStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public async ValueTask<SyncCredentialOperationStatus> StoreAsync(
        Guid spaceId,
        SyncRemoteConfiguration remoteConfiguration,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        ArgumentNullException.ThrowIfNull(remoteConfiguration);
        if (password.Length > MaximumPasswordBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(password));
        }

        byte[] bundle = Serialize(remoteConfiguration, password.Span);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PlatformSecretWriteResult result = await _secretStore.WriteAsync(
                    GetActiveSecretName(spaceId),
                    bundle,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(result.Status);
        }
        finally
        {
            _operationGate.Release();
            CryptographicOperations.ZeroMemory(bundle);
        }
    }

    public async ValueTask<SyncCredentialOpenResult> OpenAsync(
        Guid spaceId,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenCoreAsync(GetActiveSecretName(spaceId), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<SyncCredentialOperationStatus> DeleteAsync(
        Guid spaceId,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PlatformSecretWriteResult result = await _secretStore.DeleteAsync(
                    GetActiveSecretName(spaceId),
                    cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(result.Status);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<SyncCredentialOperationStatus> StageCurrentForMigrationAsync(
        Guid spaceId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        ValidatePlanId(planId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CopyToEmptyOrEqualSlotAsync(
                    GetActiveSecretName(spaceId),
                    GetMigrationSecretName(spaceId, planId, SyncMigrationCredentialSlot.Source),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<SyncCredentialOperationStatus> StageMigrationTargetAsync(
        Guid spaceId,
        Guid planId,
        SyncRemoteConfiguration remoteConfiguration,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        ValidatePlanId(planId);
        ArgumentNullException.ThrowIfNull(remoteConfiguration);
        if (password.Length > MaximumPasswordBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(password));
        }

        byte[] bundle = Serialize(remoteConfiguration, password.Span);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await WriteEmptyOrEqualSlotAsync(
                    GetMigrationSecretName(spaceId, planId, SyncMigrationCredentialSlot.Target),
                    bundle,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
            CryptographicOperations.ZeroMemory(bundle);
        }
    }

    public async ValueTask<SyncCredentialOpenResult> OpenMigrationAsync(
        Guid spaceId,
        Guid planId,
        SyncMigrationCredentialSlot slot,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        ValidatePlanId(planId);
        ValidateSlot(slot);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenCoreAsync(
                    GetMigrationSecretName(spaceId, planId, slot),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<SyncCredentialOperationStatus> CommitMigrationTargetAsync(
        Guid spaceId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        ValidatePlanId(planId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CommitMigrationTargetCoreAsync(spaceId, planId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<SyncCredentialOperationStatus> RollbackMigrationSourceAsync(
        Guid spaceId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        ValidatePlanId(planId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RestoreSourceCoreAsync(spaceId, planId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<SyncCredentialOperationStatus> DeleteMigrationSlotAsync(
        Guid spaceId,
        Guid planId,
        SyncMigrationCredentialSlot slot,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        ValidatePlanId(planId);
        ValidateSlot(slot);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PlatformSecretWriteResult result = await _secretStore.DeleteAsync(
                    GetMigrationSecretName(spaceId, planId, slot),
                    cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(result.Status);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _operationGate.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async ValueTask<SyncCredentialOpenResult> OpenCoreAsync(
        string secretName,
        CancellationToken cancellationToken)
    {
        PlatformSecretReadResult result = await _secretStore.ReadAsync(
                secretName,
                cancellationToken)
            .ConfigureAwait(false);
        SyncCredentialOperationStatus status = MapStatus(result.Status);
        if (status != SyncCredentialOperationStatus.Success)
        {
            ZeroOwnedSecret(result.Secret);
            return new SyncCredentialOpenResult(status);
        }

        try
        {
            if (!TryDeserialize(
                    result.Secret.Span,
                    out SyncRemoteConfiguration? remoteConfiguration,
                    out byte[]? ownedPassword))
            {
                return new SyncCredentialOpenResult(SyncCredentialOperationStatus.Failed);
            }

            return new SyncCredentialOpenResult(
                SyncCredentialOperationStatus.Success,
                new SyncCredentialLease(remoteConfiguration!, ownedPassword!));
        }
        finally
        {
            ZeroOwnedSecret(result.Secret);
        }
    }

    private async ValueTask<SyncCredentialOperationStatus> CopyToEmptyOrEqualSlotAsync(
        string sourceName,
        string destinationName,
        CancellationToken cancellationToken)
    {
        (SyncCredentialOperationStatus sourceStatus, byte[]? source) =
            await ReadOwnedSecretAsync(sourceName, cancellationToken).ConfigureAwait(false);
        if (sourceStatus != SyncCredentialOperationStatus.Success || source is null)
        {
            return sourceStatus;
        }

        try
        {
            return await WriteEmptyOrEqualSlotAsync(
                    destinationName,
                    source,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
        }
    }

    private async ValueTask<SyncCredentialOperationStatus> WriteEmptyOrEqualSlotAsync(
        string destinationName,
        ReadOnlyMemory<byte> bundle,
        CancellationToken cancellationToken)
    {
        (SyncCredentialOperationStatus existingStatus, byte[]? existing) =
            await ReadOwnedSecretAsync(destinationName, cancellationToken).ConfigureAwait(false);
        if (existingStatus == SyncCredentialOperationStatus.Success && existing is not null)
        {
            try
            {
                return CryptographicOperations.FixedTimeEquals(existing, bundle.Span)
                    ? SyncCredentialOperationStatus.Success
                    : SyncCredentialOperationStatus.Conflict;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existing);
            }
        }

        if (existingStatus != SyncCredentialOperationStatus.NotFound)
        {
            return existingStatus;
        }

        PlatformSecretWriteResult write = await _secretStore.WriteAsync(
                destinationName,
                bundle,
                cancellationToken)
            .ConfigureAwait(false);
        SyncCredentialOperationStatus writeStatus = MapStatus(write.Status);
        if (writeStatus != SyncCredentialOperationStatus.Success)
        {
            return writeStatus;
        }

        return await VerifySecretAsync(destinationName, bundle, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<SyncCredentialOperationStatus> CommitMigrationTargetCoreAsync(
        Guid spaceId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        string sourceName = GetMigrationSecretName(
            spaceId,
            planId,
            SyncMigrationCredentialSlot.Source);
        string targetName = GetMigrationSecretName(
            spaceId,
            planId,
            SyncMigrationCredentialSlot.Target);
        string activeName = GetActiveSecretName(spaceId);
        (SyncCredentialOperationStatus sourceStatus, byte[]? source) =
            await ReadOwnedSecretAsync(sourceName, cancellationToken).ConfigureAwait(false);
        if (sourceStatus != SyncCredentialOperationStatus.Success || source is null)
        {
            return sourceStatus;
        }

        (SyncCredentialOperationStatus targetStatus, byte[]? target) =
            await ReadOwnedSecretAsync(targetName, cancellationToken).ConfigureAwait(false);
        if (targetStatus != SyncCredentialOperationStatus.Success || target is null)
        {
            CryptographicOperations.ZeroMemory(source);
            return targetStatus;
        }

        try
        {
            (SyncCredentialOperationStatus activeStatus, byte[]? active) =
                await ReadOwnedSecretAsync(activeName, cancellationToken).ConfigureAwait(false);
            if (activeStatus != SyncCredentialOperationStatus.Success || active is null)
            {
                return activeStatus;
            }

            try
            {
                if (CryptographicOperations.FixedTimeEquals(active, target))
                {
                    return SyncCredentialOperationStatus.Success;
                }

                if (!CryptographicOperations.FixedTimeEquals(active, source))
                {
                    return SyncCredentialOperationStatus.Conflict;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(active);
            }

            PlatformSecretWriteResult write = await _secretStore.WriteAsync(
                    activeName,
                    target,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncCredentialOperationStatus writeStatus = MapStatus(write.Status);
            if (writeStatus != SyncCredentialOperationStatus.Success)
            {
                return writeStatus;
            }

            SyncCredentialOperationStatus verified = await VerifySecretAsync(
                    activeName,
                    target,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (verified == SyncCredentialOperationStatus.Success)
            {
                return verified;
            }

            _ = await RestoreRawAsync(activeName, source, CancellationToken.None)
                .ConfigureAwait(false);
            return SyncCredentialOperationStatus.Failed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
            CryptographicOperations.ZeroMemory(target);
        }
    }

    private async ValueTask<SyncCredentialOperationStatus> RestoreSourceCoreAsync(
        Guid spaceId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        (SyncCredentialOperationStatus status, byte[]? source) =
            await ReadOwnedSecretAsync(
                    GetMigrationSecretName(
                        spaceId,
                        planId,
                        SyncMigrationCredentialSlot.Source),
                    cancellationToken)
                .ConfigureAwait(false);
        if (status != SyncCredentialOperationStatus.Success || source is null)
        {
            return status;
        }

        try
        {
            return await RestoreRawAsync(
                    GetActiveSecretName(spaceId),
                    source,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
        }
    }

    private async ValueTask<SyncCredentialOperationStatus> RestoreRawAsync(
        string activeName,
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        PlatformSecretWriteResult write = await _secretStore.WriteAsync(
                activeName,
                source,
                cancellationToken)
            .ConfigureAwait(false);
        SyncCredentialOperationStatus status = MapStatus(write.Status);
        return status == SyncCredentialOperationStatus.Success
            ? await VerifySecretAsync(activeName, source, CancellationToken.None)
                .ConfigureAwait(false)
            : status;
    }

    private async ValueTask<SyncCredentialOperationStatus> VerifySecretAsync(
        string name,
        ReadOnlyMemory<byte> expected,
        CancellationToken cancellationToken)
    {
        (SyncCredentialOperationStatus status, byte[]? actual) =
            await ReadOwnedSecretAsync(name, cancellationToken).ConfigureAwait(false);
        if (status != SyncCredentialOperationStatus.Success || actual is null)
        {
            return status;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, expected.Span)
                ? SyncCredentialOperationStatus.Success
                : SyncCredentialOperationStatus.Failed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private async ValueTask<(SyncCredentialOperationStatus Status, byte[]? Secret)>
        ReadOwnedSecretAsync(
            string name,
            CancellationToken cancellationToken)
    {
        PlatformSecretReadResult result = await _secretStore.ReadAsync(name, cancellationToken)
            .ConfigureAwait(false);
        SyncCredentialOperationStatus status = MapStatus(result.Status);
        try
        {
            return status == SyncCredentialOperationStatus.Success
                ? (status, result.Secret.ToArray())
                : (status, null);
        }
        finally
        {
            ZeroOwnedSecret(result.Secret);
        }
    }

    private static string GetActiveSecretName(Guid spaceId) => $"sync/webdav/{spaceId:N}";

    private static string GetMigrationSecretName(
        Guid spaceId,
        Guid planId,
        SyncMigrationCredentialSlot slot)
    {
        ValidateSlot(slot);
        string role = slot == SyncMigrationCredentialSlot.Source ? "source" : "target";
        return $"sync/webdav/{spaceId:N}/migration/{planId:N}/{role}";
    }

    private static byte[] Serialize(
        SyncRemoteConfiguration remoteConfiguration,
        ReadOnlySpan<byte> password)
    {
        string endpoint = remoteConfiguration.Endpoint.AbsoluteUri;
        string remoteRoot = remoteConfiguration.RemoteRoot;
        string username = remoteConfiguration.Username;
        string certificatePin = remoteConfiguration.CertificateSha256Pin ?? string.Empty;
        int endpointLength = StrictUtf8.GetByteCount(endpoint);
        int remoteRootLength = StrictUtf8.GetByteCount(remoteRoot);
        int usernameLength = StrictUtf8.GetByteCount(username);
        int certificatePinLength = StrictUtf8.GetByteCount(certificatePin);
        ValidateFieldLength(endpointLength, nameof(remoteConfiguration));
        ValidateFieldLength(remoteRootLength, nameof(remoteConfiguration));
        ValidateFieldLength(usernameLength, nameof(remoteConfiguration));
        ValidateFieldLength(certificatePinLength, nameof(remoteConfiguration));
        ValidateFieldLength(password.Length, nameof(password));
        int totalLength = checked(
            HeaderLength + endpointLength + remoteRootLength + usernameLength +
            certificatePinLength + password.Length);
        if (totalLength > MaximumBundleBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(password),
                "The protected WebDAV connection bundle is too large.");
        }

        byte[] bundle = GC.AllocateUninitializedArray<byte>(totalLength);
        "SBWC"u8.CopyTo(bundle);
        bundle[4] = BundleVersion;
        bundle[5] = remoteConfiguration.AllowInsecureLoopback
            ? InsecureLoopbackFlag
            : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(bundle.AsSpan(6), (ushort)endpointLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bundle.AsSpan(8), (ushort)remoteRootLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bundle.AsSpan(10), (ushort)usernameLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bundle.AsSpan(12), (ushort)certificatePinLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bundle.AsSpan(14), (ushort)password.Length);
        int offset = HeaderLength;
        offset += StrictUtf8.GetBytes(endpoint, bundle.AsSpan(offset, endpointLength));
        offset += StrictUtf8.GetBytes(remoteRoot, bundle.AsSpan(offset, remoteRootLength));
        offset += StrictUtf8.GetBytes(username, bundle.AsSpan(offset, usernameLength));
        offset += StrictUtf8.GetBytes(
            certificatePin,
            bundle.AsSpan(offset, certificatePinLength));
        password.CopyTo(bundle.AsSpan(offset, password.Length));
        return bundle;
    }

    private static bool TryDeserialize(
        ReadOnlySpan<byte> bundle,
        out SyncRemoteConfiguration? remoteConfiguration,
        out byte[]? password)
    {
        remoteConfiguration = null;
        password = null;
        byte[]? canonical = null;
        try
        {
            if (bundle.Length is < HeaderLength or > MaximumBundleBytes ||
                !bundle[..4].SequenceEqual("SBWC"u8) ||
                bundle[4] != BundleVersion ||
                (bundle[5] & ~InsecureLoopbackFlag) != 0)
            {
                return false;
            }

            int endpointLength = BinaryPrimitives.ReadUInt16LittleEndian(bundle[6..]);
            int remoteRootLength = BinaryPrimitives.ReadUInt16LittleEndian(bundle[8..]);
            int usernameLength = BinaryPrimitives.ReadUInt16LittleEndian(bundle[10..]);
            int certificatePinLength = BinaryPrimitives.ReadUInt16LittleEndian(bundle[12..]);
            int passwordLength = BinaryPrimitives.ReadUInt16LittleEndian(bundle[14..]);
            int expectedLength = checked(
                HeaderLength + endpointLength + remoteRootLength + usernameLength +
                certificatePinLength + passwordLength);
            if (expectedLength != bundle.Length || endpointLength == 0 ||
                remoteRootLength == 0 || passwordLength > MaximumPasswordBytes ||
                certificatePinLength is not 0 and not 64)
            {
                return false;
            }

            int offset = HeaderLength;
            string endpoint = Decode(bundle, ref offset, endpointLength);
            string remoteRoot = Decode(bundle, ref offset, remoteRootLength);
            string username = Decode(bundle, ref offset, usernameLength);
            string? certificatePin = certificatePinLength == 0
                ? null
                : Decode(bundle, ref offset, certificatePinLength);
            password = bundle.Slice(offset, passwordLength).ToArray();
            remoteConfiguration = new SyncRemoteConfiguration(
                new Uri(endpoint, UriKind.Absolute),
                remoteRoot,
                username,
                certificatePin,
                (bundle[5] & InsecureLoopbackFlag) != 0);
            canonical = Serialize(remoteConfiguration, password);
            if (!CryptographicOperations.FixedTimeEquals(canonical, bundle))
            {
                CryptographicOperations.ZeroMemory(password);
                password = null;
                remoteConfiguration = null;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or DecoderFallbackException or OverflowException or UriFormatException)
        {
            if (password is not null)
            {
                CryptographicOperations.ZeroMemory(password);
                password = null;
            }

            remoteConfiguration = null;
            return false;
        }
        finally
        {
            if (canonical is not null)
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
        }
    }

    private static string Decode(ReadOnlySpan<byte> bundle, ref int offset, int length)
    {
        string value = StrictUtf8.GetString(bundle.Slice(offset, length));
        offset += length;
        return value;
    }

    private static void ValidateFieldLength(int length, string parameterName)
    {
        if ((uint)length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateSpaceId(Guid spaceId) =>
        ArgumentOutOfRangeException.ThrowIfEqual(spaceId, Guid.Empty);

    private static void ValidatePlanId(Guid planId) =>
        ArgumentOutOfRangeException.ThrowIfEqual(planId, Guid.Empty);

    private static void ValidateSlot(SyncMigrationCredentialSlot slot)
    {
        if (!Enum.IsDefined(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private static SyncCredentialOperationStatus MapStatus(
        PlatformSecretStoreStatus status) => status switch
        {
            PlatformSecretStoreStatus.Success => SyncCredentialOperationStatus.Success,
            PlatformSecretStoreStatus.NotFound => SyncCredentialOperationStatus.NotFound,
            PlatformSecretStoreStatus.AccessDenied => SyncCredentialOperationStatus.AccessDenied,
            _ => SyncCredentialOperationStatus.Failed,
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
