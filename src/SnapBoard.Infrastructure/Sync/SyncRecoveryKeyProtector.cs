using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Infrastructure.Sync;

public sealed record SyncRecoveryKdfParameters(
    int MemoryKiB = 64 * 1024,
    int Iterations = 3,
    int Parallelism = 1);

public static class SyncRecoveryKeyProtector
{
    private const int FormatVersion = 1;
    private const int SaltSize = 16;
    private const int MinimumMemoryKiB = 8 * 1024;
    private const int MaximumMemoryKiB = 256 * 1024;
    private const int MinimumIterations = 2;
    private const int MaximumIterations = 10;
    private const int MaximumParallelism = 4;
    private const int MaximumRecoveryCodeBytes = 256;
    private const int MaximumEnvelopeBytes = 64 * 1024;
    private const string KdfName = "argon2id-v1";

    public static ValueTask<byte[]> WrapAsync(
        ReadOnlyMemory<byte> masterKey,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken,
        SyncRecoveryKdfParameters? parameters = null)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(SyncProtocol.NonceSize);
        return WrapAsync(
            masterKey,
            recoveryCode,
            parameters ?? new SyncRecoveryKdfParameters(),
            salt,
            nonce,
            cancellationToken);
    }

    public static async ValueTask<byte[]> UnwrapAsync(
        ReadOnlyMemory<byte> serializedEnvelope,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryCode(recoveryCode.Span);
        if (serializedEnvelope.IsEmpty || serializedEnvelope.Length > MaximumEnvelopeBytes)
        {
            throw new SyncCryptographicException("The recovery envelope size is invalid.");
        }

        SyncRecoveryEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                    serializedEnvelope.Span,
                    SyncJsonContext.Default.SyncRecoveryEnvelope) ??
                throw new SyncCryptographicException("The recovery envelope is empty.");
        }
        catch (JsonException exception)
        {
            throw new SyncCryptographicException("The recovery envelope is malformed.", exception);
        }

        SyncRecoveryKdfParameters parameters = new(
            envelope.MemoryKiB,
            envelope.Iterations,
            envelope.Parallelism);
        ValidateEnvelope(envelope, parameters);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] wrappingKey = await DeriveWrappingKeyAsync(
                recoveryCode,
                envelope.Salt,
                parameters)
            .ConfigureAwait(false);
        byte[] masterKey = GC.AllocateUninitializedArray<byte>(SyncProtocol.MasterKeySize);
        byte[] associatedData = BuildAssociatedData(parameters);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using AesGcm aes = new(wrappingKey, SyncProtocol.AuthenticationTagSize);
            aes.Decrypt(
                envelope.Nonce,
                envelope.Ciphertext,
                envelope.AuthenticationTag,
                masterKey,
                associatedData);
            return masterKey;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw new SyncCryptographicException(
                "The recovery code is incorrect or the envelope was modified.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    internal static async ValueTask<byte[]> WrapAsync(
        ReadOnlyMemory<byte> masterKey,
        ReadOnlyMemory<byte> recoveryCode,
        SyncRecoveryKdfParameters parameters,
        ReadOnlyMemory<byte> salt,
        ReadOnlyMemory<byte> nonce,
        CancellationToken cancellationToken)
    {
        if (masterKey.Length != SyncProtocol.MasterKeySize)
        {
            throw new ArgumentException("A sync master key must be 256 bits.", nameof(masterKey));
        }

        ValidateRecoveryCode(recoveryCode.Span);
        ValidateParameters(parameters);
        if (salt.Length != SaltSize)
        {
            throw new ArgumentException("The Argon2id salt must be 128 bits.", nameof(salt));
        }

        if (nonce.Length != SyncProtocol.NonceSize)
        {
            throw new ArgumentException("AES-GCM requires a 96-bit nonce.", nameof(nonce));
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] wrappingKey = await DeriveWrappingKeyAsync(recoveryCode, salt, parameters)
            .ConfigureAwait(false);
        byte[] ciphertext = GC.AllocateUninitializedArray<byte>(SyncProtocol.MasterKeySize);
        byte[] tag = GC.AllocateUninitializedArray<byte>(SyncProtocol.AuthenticationTagSize);
        byte[] associatedData = BuildAssociatedData(parameters);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using AesGcm aes = new(wrappingKey, SyncProtocol.AuthenticationTagSize);
            aes.Encrypt(
                nonce.Span,
                masterKey.Span,
                ciphertext,
                tag,
                associatedData);
            SyncRecoveryEnvelope envelope = new(
                FormatVersion,
                KdfName,
                parameters.MemoryKiB,
                parameters.Iterations,
                parameters.Parallelism,
                salt.ToArray(),
                SyncProtocol.EncryptionAlgorithm,
                nonce.ToArray(),
                ciphertext,
                tag);
            return JsonSerializer.SerializeToUtf8Bytes(
                envelope,
                SyncJsonContext.Default.SyncRecoveryEnvelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    private static async ValueTask<byte[]> DeriveWrappingKeyAsync(
        ReadOnlyMemory<byte> recoveryCode,
        ReadOnlyMemory<byte> salt,
        SyncRecoveryKdfParameters parameters)
    {
        byte[] passwordCopy = recoveryCode.ToArray();
        byte[] saltCopy = salt.ToArray();
        try
        {
            using Argon2id argon2 = new(passwordCopy)
            {
                Salt = saltCopy,
                MemorySize = parameters.MemoryKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism,
            };
            return await argon2.GetBytesAsync(SyncProtocol.MasterKeySize).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordCopy);
            CryptographicOperations.ZeroMemory(saltCopy);
        }
    }

    private static byte[] BuildAssociatedData(SyncRecoveryKdfParameters parameters) =>
        Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"snapboard-recovery\nformat={FormatVersion}\nkdf={KdfName}\nmemory={parameters.MemoryKiB}\niterations={parameters.Iterations}\nparallelism={parameters.Parallelism}\nalgorithm={SyncProtocol.EncryptionAlgorithm}\n"));

    private static void ValidateEnvelope(
        SyncRecoveryEnvelope envelope,
        SyncRecoveryKdfParameters parameters)
    {
        ValidateParameters(parameters);
        if (envelope.FormatVersion != FormatVersion ||
            !string.Equals(envelope.Kdf, KdfName, StringComparison.Ordinal) ||
            !string.Equals(
                envelope.Algorithm,
                SyncProtocol.EncryptionAlgorithm,
                StringComparison.Ordinal) ||
            envelope.Salt is null || envelope.Salt.Length != SaltSize ||
            envelope.Nonce is null || envelope.Nonce.Length != SyncProtocol.NonceSize ||
            envelope.Ciphertext is null ||
            envelope.Ciphertext.Length != SyncProtocol.MasterKeySize ||
            envelope.AuthenticationTag is null ||
            envelope.AuthenticationTag.Length != SyncProtocol.AuthenticationTagSize)
        {
            throw new SyncCryptographicException("The recovery envelope is unsupported or invalid.");
        }
    }

    private static void ValidateParameters(SyncRecoveryKdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.MemoryKiB is < MinimumMemoryKiB or > MaximumMemoryKiB ||
            parameters.Iterations is < MinimumIterations or > MaximumIterations ||
            parameters.Parallelism is < 1 or > MaximumParallelism ||
            parameters.MemoryKiB < parameters.Parallelism * 8)
        {
            throw new SyncCryptographicException("The recovery KDF parameters are outside limits.");
        }
    }

    private static void ValidateRecoveryCode(ReadOnlySpan<byte> recoveryCode)
    {
        if (recoveryCode.Length is < 16 or > MaximumRecoveryCodeBytes)
        {
            throw new ArgumentException(
                "A recovery code must contain between 16 and 256 bytes.",
                nameof(recoveryCode));
        }
    }
}
