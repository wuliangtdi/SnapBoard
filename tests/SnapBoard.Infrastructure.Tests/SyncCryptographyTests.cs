using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnapBoard.Infrastructure.Sync;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Infrastructure.Tests;

public sealed class SyncCryptographyTests
{
    private static readonly Guid SpaceId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EventId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void FixedVectorRoundTripsAndIsStable()
    {
        byte[] masterKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        byte[] nonce = Enumerable.Range(0xA0, 12).Select(value => (byte)value).ToArray();
        byte[] plaintext = Encoding.UTF8.GetBytes("SnapBoard fixed sync vector v1");
        SyncObjectDescriptor descriptor = CreateEventDescriptor();
        byte[] encrypted = SyncObjectEncryptor.Encrypt(
            plaintext,
            descriptor,
            masterKey,
            nonce);
        SyncEncryptedObjectEnvelope envelope = JsonSerializer.Deserialize(
            encrypted,
            SyncJsonContext.Default.SyncEncryptedObjectEnvelope)!;
        byte[] decrypted = SyncObjectEncryptor.Decrypt(encrypted, descriptor, masterKey);
        string keyedBlobId = SyncKeyDerivation.ComputeKeyedBlobId(
            masterKey,
            SpaceId,
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] eventKey = new byte[32];
        byte[] blobKey = new byte[32];
        SyncKeyDerivation.DeriveEventKey(masterKey, SpaceId, eventKey);
        SyncKeyDerivation.DeriveBlobKey(masterKey, SpaceId, blobKey);

        Assert.Equal(plaintext, decrypted);
        Assert.Equal(
            "D8BFCE6A512DC1115EF05123EC865715E87CD426868F821649F86185F8616649",
            Convert.ToHexString(eventKey));
        Assert.Equal(
            "B7E73709CF78F9946972B41256F0D60EBE0EC562AC4AADD9DFEFD053BE418D35",
            Convert.ToHexString(blobKey));
        Assert.Equal(
            "2C4C46A847C67111F7A6CFF61DB5FE590B74AA98AC0759065DF6B5AB9299",
            Convert.ToHexString(envelope.Ciphertext));
        Assert.Equal(
            "8EFE6C2B0BEFFBD1104C076BE30EF027",
            Convert.ToHexString(envelope.AuthenticationTag));
        Assert.Equal(
            "827b30ed02e1e8c66535068d173425b0390d51567a5908e90576a4b76c624902",
            keyedBlobId);
        Assert.Equal(
            "snapboard-sync-aad\n" +
            "protocol=1\n" +
            "space=11111111111111111111111111111111\n" +
            "device=22222222222222222222222222222222\n" +
            "type=2\n" +
            "sequence=00000000000000000042\n" +
            "object=33333333333333333333333333333333\n" +
            "key=1\n",
            Encoding.UTF8.GetString(SyncObjectEncryptor.BuildAssociatedData(descriptor)));

        CryptographicOperations.ZeroMemory(masterKey);
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(decrypted);
        CryptographicOperations.ZeroMemory(eventKey);
        CryptographicOperations.ZeroMemory(blobKey);
    }

    [Fact]
    public void AnyExpectedAadChangeFailsBeforeOrDuringAuthentication()
    {
        byte[] masterKey = RandomNumberGenerator.GetBytes(32);
        SyncObjectDescriptor descriptor = CreateEventDescriptor();
        byte[] encrypted = SyncObjectEncryptor.Encrypt(
            "secret"u8,
            descriptor,
            masterKey);

        SyncObjectDescriptor changed = descriptor with { Sequence = descriptor.Sequence + 1 };

        Assert.Throws<SyncCryptographicException>(() =>
            SyncObjectEncryptor.Decrypt(encrypted, changed, masterKey));
        CryptographicOperations.ZeroMemory(masterKey);
    }

    [Fact]
    public void CiphertextAndTagTamperingFailClosed()
    {
        byte[] masterKey = RandomNumberGenerator.GetBytes(32);
        SyncObjectDescriptor descriptor = CreateEventDescriptor();
        byte[] encrypted = SyncObjectEncryptor.Encrypt("secret"u8, descriptor, masterKey);
        SyncEncryptedObjectEnvelope envelope = JsonSerializer.Deserialize(
            encrypted,
            SyncJsonContext.Default.SyncEncryptedObjectEnvelope)!;
        envelope.AuthenticationTag[0] ^= 0x80;
        byte[] tampered = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            SyncJsonContext.Default.SyncEncryptedObjectEnvelope);

        Assert.Throws<SyncCryptographicException>(() =>
            SyncObjectEncryptor.Decrypt(tampered, descriptor, masterKey));
        CryptographicOperations.ZeroMemory(masterKey);
    }

    [Fact]
    public void UnknownEnvelopeFieldsAreRejected()
    {
        byte[] masterKey = RandomNumberGenerator.GetBytes(32);
        SyncObjectDescriptor descriptor = CreateEventDescriptor();
        byte[] encrypted = SyncObjectEncryptor.Encrypt("secret"u8, descriptor, masterKey);
        string json = Encoding.UTF8.GetString(encrypted);
        byte[] withUnknownField = Encoding.UTF8.GetBytes(
            json.Insert(json.Length - 1, ",\"downgrade\":true"));

        Assert.Throws<SyncCryptographicException>(() =>
            SyncObjectEncryptor.Decrypt(withUnknownField, descriptor, masterKey));
        CryptographicOperations.ZeroMemory(masterKey);
    }

    private static SyncObjectDescriptor CreateEventDescriptor() => new(
        SyncProtocol.CurrentVersion,
        SpaceId,
        DeviceId,
        SyncObjectType.Event,
        42,
        EventId.ToString("N"),
        1);
}
