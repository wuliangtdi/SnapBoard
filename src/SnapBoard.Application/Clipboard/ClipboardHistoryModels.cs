using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Application.Clipboard;

public enum ClipboardHistoryDisplayCategory
{
    Text = 1,
    Image = 2,
    Code = 3,
    Link = 4,
}

public enum ClipboardStoredBitmapEncoding
{
    DeviceIndependentBitmap = 1,
    DeviceIndependentBitmapV5 = 2,
    PortableNetworkGraphics = 3,
    TaggedImageFileFormat = 4,
}

public sealed record ClipboardCapturedFormat(
    string Identifier,
    string Name,
    bool IsAvailable);

public sealed record ClipboardCapturedRepresentation(
    ClipboardContentKind Kind,
    string MediaType,
    string? Text,
    ReadOnlyMemory<byte> Data,
    ClipboardStoredBitmapEncoding? BitmapEncoding = null,
    int Width = 0,
    int Height = 0,
    ushort BitsPerPixel = 0)
{
    public long SizeBytes => Text is null
        ? Data.Length
        : System.Text.Encoding.UTF8.GetByteCount(Text);
}

public sealed class ClipboardCapturedItem
{
    public required ClipboardItemId Id { get; init; }

    public required ulong SequenceNumber { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    public int? SourceProcessId { get; init; }

    public string? SourceProcessName { get; init; }

    public string? SourceExecutablePath { get; init; }

    public string? SourceApplicationUserModelId { get; init; }

    public string? SourcePackageFamilyName { get; init; }

    public int SourceAccessStatus { get; init; }

    public int SourceAttributionKind { get; init; }

    public required ClipboardContentHash ContentHash { get; init; }

    public required ClipboardContentKind PrimaryKind { get; init; }

    public required ClipboardHistoryDisplayCategory DisplayCategory { get; init; }

    public required string PreviewText { get; init; }

    public required string SearchableText { get; init; }

    public IReadOnlyList<ClipboardCapturedRepresentation> Representations { get; init; }
        = Array.Empty<ClipboardCapturedRepresentation>();

    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ClipboardCapturedFormat> Formats { get; init; }
        = Array.Empty<ClipboardCapturedFormat>();

    public long TotalSizeBytes { get; init; }
}

public sealed record ClipboardHistoryCursor(
    bool IsPinned,
    long CapturedAtUnixMilliseconds,
    ClipboardItemId Id,
    long? SearchOrderKey = null);

public sealed class ClipboardHistoryQuery
{
    public string SearchText { get; init; } = string.Empty;

    public ClipboardHistoryDisplayCategory? DisplayCategory { get; init; }

    public IReadOnlySet<ClipboardContentKind>? ContentKinds { get; init; }

    public string? SourceApplication { get; init; }

    public DateTimeOffset? CapturedAfter { get; init; }

    public DateTimeOffset? CapturedBefore { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public bool? IsPinned { get; init; }

    public ClipboardHistoryCursor? Cursor { get; init; }

    public int PageSize { get; init; } = 50;

    public bool NewestFirst { get; init; } = true;

    public bool IncludeSearchResultCount { get; init; }
}

public sealed record ClipboardHistoryItemSummary(
    ClipboardItemId Id,
    ClipboardContentKind ContentKind,
    ClipboardHistoryDisplayCategory DisplayCategory,
    DateTimeOffset CapturedAt,
    string SourceApplication,
    string PreviewText,
    bool IsPinned,
    IReadOnlyList<string> Tags,
    long UseCount,
    DateTimeOffset? LastUsedAt,
    long TotalSizeBytes,
    bool HasThumbnail,
    string? SourceExecutablePath = null,
    string? SourceApplicationUserModelId = null,
    string? SourcePackageFamilyName = null,
    int SourceAttributionKind = 0);

public sealed record ClipboardHistoryPage(
    IReadOnlyList<ClipboardHistoryItemSummary> Items,
    ClipboardHistoryCursor? NextCursor,
    // -1 表示全文搜索热路径未请求精确总数，避免为显示首屏扫描全部命中。
    long TotalCount);

public sealed record ClipboardHistoryBitmap(
    ClipboardStoredBitmapEncoding Encoding,
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    ushort BitsPerPixel);

public sealed record ClipboardHistoryContent(
    ClipboardItemId Id,
    string? Text,
    ReadOnlyMemory<byte> Html,
    ReadOnlyMemory<byte> RichText,
    ClipboardHistoryBitmap? Bitmap,
    IReadOnlyList<string> FilePaths);

public sealed record ClipboardHistorySaveResult(
    ClipboardItemId ItemId,
    bool WasMerged);

public sealed record ClipboardHistoryInitializationResult(
    bool RecoveredCorruptDatabase,
    string? RecoveryDirectory = null,
    string? DiagnosticCode = null);

public enum ClipboardHistoryChangeKind
{
    Added = 1,
    Updated = 2,
    Deleted = 3,
    Cleared = 4,
    SettingChanged = 5,
}

public sealed record ClipboardHistoryChangedEvent(
    ClipboardHistoryChangeKind Kind,
    ClipboardItemId? ItemId = null,
    string? SettingKey = null);
