namespace SnapBoard.Sync.Contracts;

/// <summary>WebDAV 上唯一允许保存的内容信封。</summary>
public sealed record SyncEncryptedObjectEnvelope(
    int FormatVersion,
    int ProtocolVersion,
    Guid SpaceId,
    Guid DeviceId,
    SyncObjectType ObjectType,
    long Sequence,
    string ObjectId,
    string Algorithm,
    int KeyVersion,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] AuthenticationTag);

public sealed record SyncObjectDescriptor(
    int ProtocolVersion,
    Guid SpaceId,
    Guid DeviceId,
    SyncObjectType ObjectType,
    long Sequence,
    string ObjectId,
    int KeyVersion);
