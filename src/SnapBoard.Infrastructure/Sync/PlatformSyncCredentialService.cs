using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SnapBoard.Application.Sync;
using SnapBoard.Platform.Abstractions.Security;

namespace SnapBoard.Infrastructure.Sync;

public sealed class PlatformSyncCredentialService : ISyncCredentialService
{
    private const byte BundleVersion = 1;
    private const byte InsecureLoopbackFlag = 1;
    private const int HeaderLength = 16;
    private const int MaximumBundleBytes = 5 * 512;
    private const int MaximumPasswordBytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly IPlatformSecretStore _secretStore;

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
        try
        {
            PlatformSecretWriteResult result = await _secretStore.WriteAsync(
                    GetSecretName(spaceId),
                    bundle,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(result.Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundle);
        }
    }

    public async ValueTask<SyncCredentialOpenResult> OpenAsync(
        Guid spaceId,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        PlatformSecretReadResult result = await _secretStore.ReadAsync(
                GetSecretName(spaceId),
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

    public async ValueTask<SyncCredentialOperationStatus> DeleteAsync(
        Guid spaceId,
        CancellationToken cancellationToken)
    {
        ValidateSpaceId(spaceId);
        PlatformSecretWriteResult result = await _secretStore.DeleteAsync(
                GetSecretName(spaceId),
                cancellationToken)
            .ConfigureAwait(false);
        return MapStatus(result.Status);
    }

    private static string GetSecretName(Guid spaceId) => $"sync/webdav/{spaceId:N}";

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
