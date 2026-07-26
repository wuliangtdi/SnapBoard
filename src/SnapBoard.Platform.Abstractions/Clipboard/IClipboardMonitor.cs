namespace SnapBoard.Platform.Abstractions.Clipboard;

/// <summary>
/// 各操作系统剪贴板监听器的统一边界。
/// </summary>
public interface IClipboardMonitor : IAsyncDisposable
{
    IAsyncEnumerable<ClipboardChangedEvent> WatchAsync(CancellationToken cancellationToken);
}
