namespace SnapBoard.Sync.Contracts;

/// <summary>加密前的不可变同步事件。时间固定为 Unix 毫秒。</summary>
public sealed record SyncEventEnvelope(
    int ProtocolVersion,
    Guid SpaceId,
    Guid EventId,
    Guid DeviceId,
    long Sequence,
    long LogicalTimestamp,
    long CreatedAtUnixMilliseconds,
    SyncChangeKind ChangeKind,
    Guid ItemId,
    SyncClipboardItemPayload? Item,
    string[]? Tags,
    bool? IsPinned,
    SyncSettingPayload? Setting = null);
