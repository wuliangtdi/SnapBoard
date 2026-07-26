namespace SnapBoard.Sync.Contracts;

/// <summary>
/// 加密前的同步事件信封。正文和 Blob 内容不会以明文形式写入 WebDAV；
/// 此类型只定义跨版本稳定的协议字段。
/// </summary>
public sealed record SyncEventEnvelope(
    int ProtocolVersion,
    Guid EventId,
    Guid DeviceId,
    long Sequence,
    DateTimeOffset CreatedAt,
    SyncPayloadKind PayloadKind,
    string PayloadObjectName);
