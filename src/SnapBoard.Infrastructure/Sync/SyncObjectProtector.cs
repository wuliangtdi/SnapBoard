using SnapBoard.Application.Sync;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Infrastructure.Sync;

public sealed class SyncObjectProtector : ISyncObjectProtector
{
    public byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        SyncObjectDescriptor descriptor,
        ReadOnlySpan<byte> masterKey) =>
        SyncObjectEncryptor.Encrypt(plaintext, descriptor, masterKey);

    public byte[] Decrypt(
        ReadOnlySpan<byte> encryptedEnvelope,
        SyncObjectDescriptor expectedDescriptor,
        ReadOnlySpan<byte> masterKey) =>
        SyncObjectEncryptor.Decrypt(encryptedEnvelope, expectedDescriptor, masterKey);

    public SyncObjectDescriptor ReadDescriptor(ReadOnlySpan<byte> encryptedEnvelope) =>
        SyncObjectEncryptor.ReadDescriptor(encryptedEnvelope);

    public string ComputeKeyedBlobId(
        ReadOnlySpan<byte> masterKey,
        Guid spaceId,
        string plaintextSha256) =>
        SyncKeyDerivation.ComputeKeyedBlobId(masterKey, spaceId, plaintextSha256);
}
