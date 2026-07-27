using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Application.Clipboard;

/// <summary>
/// 本地历史存储端口。实现必须隐藏数据库 Provider 和文件布局，调用方只处理
/// Application DTO；所有写入由实现负责串行化并保持事务一致性。
/// </summary>
public interface IClipboardHistoryStore
{
    ValueTask<ClipboardHistoryInitializationResult> InitializeAsync(
        CancellationToken cancellationToken);

    ValueTask<ClipboardHistorySaveResult> SaveAsync(
        ClipboardCapturedItem item,
        CancellationToken cancellationToken);

    ValueTask<ClipboardHistoryPage> SearchAsync(
        ClipboardHistoryQuery query,
        CancellationToken cancellationToken);

    ValueTask<ClipboardHistoryContent?> GetContentAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken);

    ValueTask<ReadOnlyMemory<byte>> GetThumbnailAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken);

    ValueTask<bool> SetPinnedAsync(
        ClipboardItemId itemId,
        bool isPinned,
        CancellationToken cancellationToken);

    ValueTask<bool> SetTagsAsync(
        ClipboardItemId itemId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken);

    ValueTask<bool> SoftDeleteAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken);

    ValueTask<int> ClearAsync(
        bool includePinned,
        CancellationToken cancellationToken);

    ValueTask<int> ApplyRetentionAsync(
        ClipboardRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<bool> RecordUseAsync(
        ClipboardItemId itemId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken);

    ValueTask<string?> GetSettingAsync(
        string key,
        CancellationToken cancellationToken);

    ValueTask SetSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken);

    ValueTask<int> CleanupOrphanedBlobsAsync(CancellationToken cancellationToken);
}

public interface IClipboardHistoryService
{
    event EventHandler<ClipboardHistoryChangedEvent>? HistoryChanged;

    ValueTask<ClipboardHistoryInitializationResult> InitializeAsync(
        CancellationToken cancellationToken);

    ValueTask<ClipboardHistoryPage> SearchAsync(
        ClipboardHistoryQuery query,
        CancellationToken cancellationToken);

    ValueTask<ClipboardHistoryContent?> GetContentAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken);

    ValueTask<ReadOnlyMemory<byte>> GetThumbnailAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken);

    ValueTask<bool> SetPinnedAsync(
        ClipboardItemId itemId,
        bool isPinned,
        CancellationToken cancellationToken);

    ValueTask<bool> SetTagsAsync(
        ClipboardItemId itemId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken);

    ValueTask<int> ClearAsync(
        bool includePinned,
        CancellationToken cancellationToken);

    ValueTask<bool> RecordUseAsync(
        ClipboardItemId itemId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken);

    ValueTask<string?> GetSettingAsync(
        string key,
        CancellationToken cancellationToken);

    ValueTask SetSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken);
}
