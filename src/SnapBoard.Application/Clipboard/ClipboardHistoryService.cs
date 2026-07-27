using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Application.Clipboard;

public sealed class ClipboardHistoryChangeNotifier
{
    public event EventHandler<ClipboardHistoryChangedEvent>? Changed;

    internal void Publish(ClipboardHistoryChangedEvent change) => Changed?.Invoke(this, change);
}

public sealed class ClipboardHistoryService(
    IClipboardHistoryStore store,
    ClipboardHistoryChangeNotifier notifier) : IClipboardHistoryService
{
    public event EventHandler<ClipboardHistoryChangedEvent>? HistoryChanged
    {
        add => notifier.Changed += value;
        remove => notifier.Changed -= value;
    }

    public ValueTask<ClipboardHistoryInitializationResult> InitializeAsync(
        CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);

    public ValueTask<ClipboardHistoryPage> SearchAsync(
        ClipboardHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Page size must be between 1 and 200.");
        }

        return store.SearchAsync(query, cancellationToken);
    }

    public ValueTask<ClipboardHistoryContent?> GetContentAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken) => store.GetContentAsync(itemId, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>> GetThumbnailAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken) => store.GetThumbnailAsync(itemId, cancellationToken);

    public async ValueTask<bool> SetPinnedAsync(
        ClipboardItemId itemId,
        bool isPinned,
        CancellationToken cancellationToken)
    {
        bool updated = await store.SetPinnedAsync(itemId, isPinned, cancellationToken)
            .ConfigureAwait(false);
        if (updated)
        {
            notifier.Publish(new ClipboardHistoryChangedEvent(
                ClipboardHistoryChangeKind.Updated,
                itemId));
        }

        return updated;
    }

    public async ValueTask<bool> SetTagsAsync(
        ClipboardItemId itemId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        bool updated = await store.SetTagsAsync(itemId, tags, cancellationToken)
            .ConfigureAwait(false);
        if (updated)
        {
            notifier.Publish(new ClipboardHistoryChangedEvent(
                ClipboardHistoryChangeKind.Updated,
                itemId));
        }

        return updated;
    }

    public async ValueTask<bool> DeleteAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken)
    {
        bool deleted = await store.SoftDeleteAsync(itemId, cancellationToken)
            .ConfigureAwait(false);
        if (deleted)
        {
            notifier.Publish(new ClipboardHistoryChangedEvent(
                ClipboardHistoryChangeKind.Deleted,
                itemId));
        }

        return deleted;
    }

    public async ValueTask<int> ClearAsync(
        bool includePinned,
        CancellationToken cancellationToken)
    {
        int deleted = await store.ClearAsync(includePinned, cancellationToken)
            .ConfigureAwait(false);
        if (deleted > 0)
        {
            notifier.Publish(new ClipboardHistoryChangedEvent(ClipboardHistoryChangeKind.Cleared));
        }

        return deleted;
    }

    public ValueTask<bool> RecordUseAsync(
        ClipboardItemId itemId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken) => store.RecordUseAsync(itemId, usedAt, cancellationToken);

    public ValueTask<string?> GetSettingAsync(
        string key,
        CancellationToken cancellationToken)
    {
        ValidateSettingKey(key);
        return store.GetSettingAsync(key, cancellationToken);
    }

    public ValueTask SetSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        ValidateSettingKey(key);
        ArgumentNullException.ThrowIfNull(value);
        return store.SetSettingAsync(key, value, cancellationToken);
    }

    private static void ValidateSettingKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128 || key.Any(char.IsControl))
        {
            throw new ArgumentException("Setting key is invalid.", nameof(key));
        }
    }
}
