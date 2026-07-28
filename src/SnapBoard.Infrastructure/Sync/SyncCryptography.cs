using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Infrastructure.Sync;

public sealed class SyncCryptographicException : CryptographicException
{
    public SyncCryptographicException(string message)
        : base(message)
    {
    }

    public SyncCryptographicException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class SyncKeyDerivation
{
    private const int DerivedKeySize = 32;
    private static ReadOnlySpan<byte> EventKeyInfo => "event-encryption"u8;
    private static ReadOnlySpan<byte> BlobKeyInfo => "blob-encryption"u8;
    private static ReadOnlySpan<byte> IdentifierKeyInfo => "identifier"u8;

    public static string ComputeKeyedBlobId(
        ReadOnlySpan<byte> masterKey,
        Guid spaceId,
        string plaintextSha256)
    {
        ValidateMasterKey(masterKey);
        if (!SyncRemoteLayout.IsLowerHex(plaintextSha256, 64))
        {
            throw new ArgumentException(
                "The plaintext Blob hash must be lowercase SHA-256 hex.",
                nameof(plaintextSha256));
        }

        byte[] hashBytes = Convert.FromHexString(plaintextSha256);
        byte[] identifierKey = GC.AllocateUninitializedArray<byte>(DerivedKeySize);
        byte[] keyedHash = GC.AllocateUninitializedArray<byte>(DerivedKeySize);
        try
        {
            DeriveKey(masterKey, spaceId, IdentifierKeyInfo, identifierKey);
            HMACSHA256.HashData(identifierKey, hashBytes, keyedHash);
            return Convert.ToHexStringLower(keyedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identifierKey);
            CryptographicOperations.ZeroMemory(keyedHash);
            CryptographicOperations.ZeroMemory(hashBytes);
        }
    }

    internal static void DeriveEventKey(
        ReadOnlySpan<byte> masterKey,
        Guid spaceId,
        Span<byte> destination) =>
        DeriveKey(masterKey, spaceId, EventKeyInfo, destination);

    internal static void DeriveBlobKey(
        ReadOnlySpan<byte> masterKey,
        Guid spaceId,
        Span<byte> destination) =>
        DeriveKey(masterKey, spaceId, BlobKeyInfo, destination);

    private static void DeriveKey(
        ReadOnlySpan<byte> masterKey,
        Guid spaceId,
        ReadOnlySpan<byte> info,
        Span<byte> destination)
    {
        ValidateMasterKey(masterKey);
        ArgumentOutOfRangeException.ThrowIfEqual(spaceId, Guid.Empty);
        if (destination.Length != DerivedKeySize)
        {
            throw new ArgumentException("A derived sync key must be 256 bits.", nameof(destination));
        }

        Span<byte> salt = stackalloc byte[50];
        "SnapBoard/sync/v1/"u8.CopyTo(salt);
        int bytesWritten = Encoding.ASCII.GetBytes(spaceId.ToString("N"), salt[18..]);
        if (bytesWritten != 32)
        {
            throw new InvalidOperationException("The sync space identifier could not be formatted.");
        }

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            destination,
            salt,
            info);
    }

    private static void ValidateMasterKey(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != SyncProtocol.MasterKeySize)
        {
            throw new ArgumentException("A sync master key must be 256 bits.", nameof(masterKey));
        }
    }
}

public static class SyncObjectEncryptor
{
    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        SyncObjectDescriptor descriptor,
        ReadOnlySpan<byte> masterKey)
    {
        Span<byte> nonce = stackalloc byte[SyncProtocol.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return Encrypt(plaintext, descriptor, masterKey, nonce);
    }

    public static byte[] Decrypt(
        ReadOnlySpan<byte> encryptedEnvelope,
        SyncObjectDescriptor expectedDescriptor,
        ReadOnlySpan<byte> masterKey)
    {
        ValidateDescriptor(expectedDescriptor);
        if (encryptedEnvelope.IsEmpty ||
            encryptedEnvelope.Length > SyncProtocol.MaximumEncryptedEnvelopeBytes)
        {
            throw new SyncCryptographicException("The encrypted sync object size is invalid.");
        }

        SyncEncryptedObjectEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                    encryptedEnvelope,
                    SyncJsonContext.Default.SyncEncryptedObjectEnvelope) ??
                throw new SyncCryptographicException("The encrypted sync envelope is empty.");
        }
        catch (JsonException exception)
        {
            throw new SyncCryptographicException(
                "The encrypted sync envelope is malformed.",
                exception);
        }

        try
        {
            ValidateEnvelope(envelope, expectedDescriptor);
            int maximumPlaintextBytes = GetMaximumPlaintextBytes(expectedDescriptor.ObjectType);
            if (envelope.Ciphertext.Length > maximumPlaintextBytes)
            {
                throw new SyncCryptographicException("The encrypted sync object exceeds its limit.");
            }

            byte[] plaintext = GC.AllocateUninitializedArray<byte>(envelope.Ciphertext.Length);
            byte[] key = GC.AllocateUninitializedArray<byte>(SyncProtocol.MasterKeySize);
            byte[] associatedData = BuildAssociatedData(expectedDescriptor);
            try
            {
                DeriveObjectKey(masterKey, expectedDescriptor, key);
                using AesGcm aes = new(key, SyncProtocol.AuthenticationTagSize);
                aes.Decrypt(
                    envelope.Nonce,
                    envelope.Ciphertext,
                    envelope.AuthenticationTag,
                    plaintext,
                    associatedData);
                return plaintext;
            }
            catch (CryptographicException exception)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new SyncCryptographicException(
                    "The encrypted sync object failed authentication.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(associatedData);
            }
        }
        finally
        {
            ZeroEnvelope(envelope);
        }
    }

    public static SyncObjectDescriptor ReadDescriptor(ReadOnlySpan<byte> encryptedEnvelope)
    {
        if (encryptedEnvelope.IsEmpty ||
            encryptedEnvelope.Length > SyncProtocol.MaximumEncryptedEnvelopeBytes)
        {
            throw new SyncCryptographicException("The encrypted sync object size is invalid.");
        }

        SyncEncryptedObjectEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                    encryptedEnvelope,
                    SyncJsonContext.Default.SyncEncryptedObjectEnvelope) ??
                throw new SyncCryptographicException("The encrypted sync envelope is empty.");
        }
        catch (JsonException exception)
        {
            throw new SyncCryptographicException(
                "The encrypted sync envelope is malformed.",
                exception);
        }

        try
        {
            SyncObjectDescriptor descriptor = new(
                envelope.ProtocolVersion,
                envelope.SpaceId,
                envelope.DeviceId,
                envelope.ObjectType,
                envelope.Sequence,
                envelope.ObjectId,
                envelope.KeyVersion);
            ValidateDescriptor(descriptor);
            ValidateEnvelope(envelope, descriptor);
            return descriptor;
        }
        finally
        {
            ZeroEnvelope(envelope);
        }
    }

    internal static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        SyncObjectDescriptor descriptor,
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> nonce)
    {
        ValidateDescriptor(descriptor);
        if (plaintext.Length > GetMaximumPlaintextBytes(descriptor.ObjectType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(plaintext),
                "The sync object exceeds its plaintext limit.");
        }

        if (nonce.Length != SyncProtocol.NonceSize)
        {
            throw new ArgumentException("AES-GCM requires a 96-bit nonce.", nameof(nonce));
        }

        byte[] nonceCopy = nonce.ToArray();
        byte[] ciphertext = GC.AllocateUninitializedArray<byte>(plaintext.Length);
        byte[] authenticationTag = GC.AllocateUninitializedArray<byte>(
            SyncProtocol.AuthenticationTagSize);
        byte[] key = GC.AllocateUninitializedArray<byte>(SyncProtocol.MasterKeySize);
        byte[] associatedData = BuildAssociatedData(descriptor);
        try
        {
            DeriveObjectKey(masterKey, descriptor, key);
            using AesGcm aes = new(key, SyncProtocol.AuthenticationTagSize);
            aes.Encrypt(
                nonceCopy,
                plaintext,
                ciphertext,
                authenticationTag,
                associatedData);
            SyncEncryptedObjectEnvelope envelope = new(
                SyncProtocol.EncryptionEnvelopeVersion,
                descriptor.ProtocolVersion,
                descriptor.SpaceId,
                descriptor.DeviceId,
                descriptor.ObjectType,
                descriptor.Sequence,
                descriptor.ObjectId,
                SyncProtocol.EncryptionAlgorithm,
                descriptor.KeyVersion,
                nonceCopy,
                ciphertext,
                authenticationTag);
            return JsonSerializer.SerializeToUtf8Bytes(
                envelope,
                SyncJsonContext.Default.SyncEncryptedObjectEnvelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(nonceCopy);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(authenticationTag);
        }
    }

    internal static byte[] BuildAssociatedData(SyncObjectDescriptor descriptor)
    {
        ValidateDescriptor(descriptor);
        string canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"snapboard-sync-aad\nprotocol={descriptor.ProtocolVersion}\nspace={descriptor.SpaceId:N}\ndevice={descriptor.DeviceId:N}\ntype={(int)descriptor.ObjectType}\nsequence={descriptor.Sequence:D20}\nobject={descriptor.ObjectId}\nkey={descriptor.KeyVersion}\n");
        return Encoding.UTF8.GetBytes(canonical);
    }

    private static void ValidateEnvelope(
        SyncEncryptedObjectEnvelope envelope,
        SyncObjectDescriptor expected)
    {
        if (envelope.FormatVersion != SyncProtocol.EncryptionEnvelopeVersion ||
            envelope.ProtocolVersion != expected.ProtocolVersion ||
            envelope.SpaceId != expected.SpaceId ||
            envelope.DeviceId != expected.DeviceId ||
            envelope.ObjectType != expected.ObjectType ||
            envelope.Sequence != expected.Sequence ||
            !string.Equals(envelope.ObjectId, expected.ObjectId, StringComparison.Ordinal) ||
            envelope.KeyVersion != expected.KeyVersion ||
            !string.Equals(
                envelope.Algorithm,
                SyncProtocol.EncryptionAlgorithm,
                StringComparison.Ordinal) ||
            envelope.Nonce is null || envelope.Nonce.Length != SyncProtocol.NonceSize ||
            envelope.Ciphertext is null ||
            envelope.AuthenticationTag is null ||
            envelope.AuthenticationTag.Length != SyncProtocol.AuthenticationTagSize)
        {
            throw new SyncCryptographicException(
                "The encrypted sync envelope does not match the expected object.");
        }
    }

    private static void ValidateDescriptor(SyncObjectDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ProtocolVersion != SyncProtocol.CurrentVersion ||
            descriptor.SpaceId == Guid.Empty ||
            descriptor.DeviceId == Guid.Empty ||
            descriptor.KeyVersion is < 1 or > 1_000_000 ||
            descriptor.Sequence < 0)
        {
            throw new ArgumentException("The sync object descriptor is invalid.", nameof(descriptor));
        }

        bool validObjectId = descriptor.ObjectType switch
        {
            SyncObjectType.Metadata =>
                descriptor.Sequence == 0 && descriptor.ObjectId == "metadata",
            SyncObjectType.Event =>
                descriptor.Sequence > 0 && IsCanonicalGuid(descriptor.ObjectId),
            SyncObjectType.Blob =>
                descriptor.Sequence == 0 && SyncRemoteLayout.IsLowerHex(descriptor.ObjectId, 64),
            SyncObjectType.Checkpoint => IsCanonicalGuid(descriptor.ObjectId),
            SyncObjectType.ProviderMigration =>
                descriptor.Sequence > 0 && IsCanonicalGuid(descriptor.ObjectId),
            _ => false,
        };
        if (!validObjectId)
        {
            throw new ArgumentException("The sync object identifier is invalid.", nameof(descriptor));
        }
    }

    private static bool IsCanonicalGuid(string? value) =>
        value is not null &&
        Guid.TryParseExact(value, "N", out Guid parsed) &&
        parsed != Guid.Empty &&
        string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);

    private static int GetMaximumPlaintextBytes(SyncObjectType objectType) => objectType switch
    {
        SyncObjectType.Blob => SyncProtocol.MaximumBlobPlaintextBytes,
        SyncObjectType.Metadata or SyncObjectType.Event or SyncObjectType.Checkpoint or
            SyncObjectType.ProviderMigration =>
            SyncProtocol.MaximumEventPlaintextBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(objectType)),
    };

    private static void DeriveObjectKey(
        ReadOnlySpan<byte> masterKey,
        SyncObjectDescriptor descriptor,
        Span<byte> destination)
    {
        if (descriptor.ObjectType == SyncObjectType.Blob)
        {
            SyncKeyDerivation.DeriveBlobKey(masterKey, descriptor.SpaceId, destination);
        }
        else
        {
            SyncKeyDerivation.DeriveEventKey(masterKey, descriptor.SpaceId, destination);
        }
    }

    private static void ZeroEnvelope(SyncEncryptedObjectEnvelope envelope)
    {
        if (envelope.Nonce is not null)
        {
            CryptographicOperations.ZeroMemory(envelope.Nonce);
        }

        if (envelope.Ciphertext is not null)
        {
            CryptographicOperations.ZeroMemory(envelope.Ciphertext);
        }

        if (envelope.AuthenticationTag is not null)
        {
            CryptographicOperations.ZeroMemory(envelope.AuthenticationTag);
        }
    }
}
