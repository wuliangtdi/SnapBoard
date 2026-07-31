namespace SnapBoard.Sync.Contracts;

public sealed record SyncRepresentationPayload(
    SyncPayloadKind Kind,
    string MediaType,
    string? Text,
    byte[]? InlineData,
    string? BlobHash,
    long SizeBytes,
    int? BitmapEncoding,
    int Width,
    int Height,
    int BitsPerPixel);

public sealed record SyncBlobReferencePayload(
    string Hash,
    string MediaType,
    long SizeBytes);

public sealed record SyncSourceApplicationIconPayload(
    SyncBlobReferencePayload Blob,
    int FormatVersion,
    int Width,
    int Height,
    int Stride);

/// <summary>
/// 剪贴板内容的加密载荷。文件系统路径有意不属于远端协议。
/// </summary>
public sealed record SyncClipboardItemPayload(
    string ContentHash,
    SyncPayloadKind PrimaryKind,
    int DisplayCategory,
    long CapturedAtUnixMilliseconds,
    string PreviewText,
    string SearchableText,
    string? SourceApplication,
    string? SourceApplicationUserModelId,
    string? SourcePackageFamilyName,
    int SourceAttributionKind,
    SyncRepresentationPayload[] Representations,
    SyncBlobReferencePayload? Thumbnail,
    long TotalSizeBytes,
    SyncSourceApplicationIconPayload? SourceApplicationIcon = null);

public sealed record SyncSpaceMetadata(
    int ProtocolVersion,
    Guid SpaceId,
    int KeyVersion,
    long CreatedAtUnixMilliseconds);

public sealed record SyncDeviceCheckpoint(
    int ProtocolVersion,
    Guid SpaceId,
    Guid DeviceId,
    long AppliedSequence,
    Guid AppliedEventId,
    long UpdatedAtUnixMilliseconds);
