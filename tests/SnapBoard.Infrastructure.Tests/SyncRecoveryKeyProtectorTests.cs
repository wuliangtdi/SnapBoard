using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnapBoard.Infrastructure.Sync;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Infrastructure.Tests;

public sealed class SyncRecoveryKeyProtectorTests
{
    private static readonly SyncRecoveryKdfParameters TestParameters = new(
        MemoryKiB: 8 * 1024,
        Iterations: 2,
        Parallelism: 1);

    [Fact]
    public async Task FixedArgon2idVectorWrapsAndUnwrapsMasterKey()
    {
        byte[] masterKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        byte[] recoveryCode = Encoding.UTF8.GetBytes("correct horse battery staple");
        byte[] salt = Enumerable.Range(0x10, 16).Select(value => (byte)value).ToArray();
        byte[] nonce = Enumerable.Range(0xA0, 12).Select(value => (byte)value).ToArray();

        byte[] wrapped = await SyncRecoveryKeyProtector.WrapAsync(
            masterKey,
            recoveryCode,
            TestParameters,
            salt,
            nonce,
            CancellationToken.None);
        SyncRecoveryEnvelope envelope = JsonSerializer.Deserialize(
            wrapped,
            SyncJsonContext.Default.SyncRecoveryEnvelope)!;
        byte[] unwrapped = await SyncRecoveryKeyProtector.UnwrapAsync(
            wrapped,
            recoveryCode,
            CancellationToken.None);
        Assert.Equal(masterKey, unwrapped);
        Assert.Equal(
            "C60452A161F1C61F837A5915D1695EA2AEE8E1C9040D487182296525BD277DC7",
            Convert.ToHexString(envelope.Ciphertext));
        Assert.Equal(
            "39C9E0D005E17C2922F99197BA7BFAE6",
            Convert.ToHexString(envelope.AuthenticationTag));

        CryptographicOperations.ZeroMemory(masterKey);
        CryptographicOperations.ZeroMemory(recoveryCode);
        CryptographicOperations.ZeroMemory(unwrapped);
    }

    [Fact]
    public async Task WrongRecoveryCodeFailsAuthentication()
    {
        byte[] masterKey = RandomNumberGenerator.GetBytes(32);
        byte[] correctCode = Encoding.UTF8.GetBytes("correct recovery code bytes");
        byte[] wrongCode = Encoding.UTF8.GetBytes("incorrect recovery code xx");
        byte[] wrapped = await SyncRecoveryKeyProtector.WrapAsync(
            masterKey,
            correctCode,
            CancellationToken.None,
            TestParameters);

        await Assert.ThrowsAsync<SyncCryptographicException>(async () =>
            await SyncRecoveryKeyProtector.UnwrapAsync(
                wrapped,
                wrongCode,
                CancellationToken.None));

        CryptographicOperations.ZeroMemory(masterKey);
        CryptographicOperations.ZeroMemory(correctCode);
        CryptographicOperations.ZeroMemory(wrongCode);
    }

    [Fact]
    public async Task MaliciousKdfParametersAreRejectedBeforeDerivation()
    {
        SyncRecoveryEnvelope envelope = new(
            1,
            "argon2id-v1",
            int.MaxValue,
            3,
            1,
            new byte[16],
            SyncProtocol.EncryptionAlgorithm,
            new byte[12],
            new byte[32],
            new byte[16]);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            SyncJsonContext.Default.SyncRecoveryEnvelope);

        await Assert.ThrowsAsync<SyncCryptographicException>(async () =>
            await SyncRecoveryKeyProtector.UnwrapAsync(
                serialized,
                "long-enough-recovery-code"u8.ToArray(),
                CancellationToken.None));
    }
}
