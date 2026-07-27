using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Desktop.Bootstrap;

internal sealed record ClipboardCaptureState(
    bool IsPaused,
    long ObservedEventCount,
    long ReadCount,
    ClipboardReadStatus? LastReadStatus,
    string? ErrorMessage = null);

/// <summary>
/// 桌面进程只启动一次剪贴板 WatchAsync。暂停记录时仍持续排空平台有界队列，
/// 但跳过正文读取，防止取消/重启一次性 watcher 导致事件停摆或句柄泄漏。
/// </summary>
internal sealed class ClipboardCaptureCoordinator(
    IClipboardMonitor monitor,
    IClipboardContentReader reader) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _captureTask;
    private long _observedEventCount;
    private long _readCount;
    private int _isPaused;
    private int _started;
    private int _disposed;

    public event Action<ClipboardCaptureState>? StateChanged;

    public bool IsPaused => Volatile.Read(ref _isPaused) != 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _captureTask = Task.Run(
            () => CaptureAsync(_shutdown.Token),
            CancellationToken.None);
        PublishState(null);
    }

    public void SetPaused(bool paused)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Exchange(ref _isPaused, paused ? 1 : 0);
        PublishState(null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            _captureTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task CaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ClipboardChangedEvent change in
                monitor.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _observedEventCount);
                if (IsPaused)
                {
                    PublishState(null);
                    continue;
                }

                ClipboardReadResult result =
                    await reader.ReadAsync(change, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _readCount);
                PublishState(result.Status);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishState(null, exception.Message);
        }
    }

    private void PublishState(
        ClipboardReadStatus? lastReadStatus,
        string? errorMessage = null)
    {
        StateChanged?.Invoke(new ClipboardCaptureState(
            IsPaused,
            Interlocked.Read(ref _observedEventCount),
            Interlocked.Read(ref _readCount),
            lastReadStatus,
            errorMessage));
    }
}
