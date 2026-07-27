using SnapBoard.Application.Clipboard;
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
internal sealed class ClipboardCaptureCoordinator : IDisposable
{
    private readonly IClipboardCaptureService? _captureService;
    private readonly IClipboardMonitor _monitor;
    private readonly IClipboardContentReader _reader;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _captureTask;
    private long _observedEventCount;
    private long _readCount;
    private int _isPaused;
    private int _started;
    private int _disposed;

    public ClipboardCaptureCoordinator(
        IClipboardMonitor monitor,
        IClipboardContentReader reader)
        : this(monitor, reader, null)
    {
    }

    public ClipboardCaptureCoordinator(
        IClipboardMonitor monitor,
        IClipboardContentReader reader,
        IClipboardCaptureService? captureService)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(reader);
        _monitor = monitor;
        _reader = reader;
        _captureService = captureService;
    }

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
                _monitor.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _observedEventCount);
                if (IsPaused)
                {
                    PublishState(null);
                    continue;
                }

                ClipboardReadResult result =
                    await _reader.ReadAsync(change, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _readCount);
                if (_captureService is not null)
                {
                    try
                    {
                        await _captureService.ProcessAsync(result, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // 单条策略/持久化异常不能终止唯一平台 watcher；正文和异常细节不进入状态消息。
                    }
                }

                PublishState(result.Status);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // 平台异常可能间接携带剪贴板正文、格式名或路径，UI 只能接收固定诊断码。
            PublishState(null, "clipboard-capture-failed");
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
