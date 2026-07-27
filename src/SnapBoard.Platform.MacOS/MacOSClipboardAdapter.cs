using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Clipboard;

namespace SnapBoard.Platform.MacOS;

/// <summary>
/// macOS 剪贴板能力的聚合适配器。Application 只依赖共享端口，
/// AppKit、Objective-C Runtime、CoreGraphics 与 Accessibility 均封装在平台层内。
/// </summary>
public sealed class MacOSClipboardAdapter :
    IClipboardMonitor,
    IClipboardContentReader,
    IClipboardWriter,
    IAutomaticPasteService,
    IDisposable
{
    private readonly object _nativeGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly MacOSClipboardSettings _settings;
    private readonly IMacOSPasteboardNative _pasteboard;
    private readonly IAsyncDelay _delay;
    private readonly MacOSClipboardEventQueue _eventQueue;
    private readonly MacOSClipboardFeedbackGuard _feedbackGuard = new();
    private readonly MacOSAutomaticPaste _automaticPaste;
    private Task? _pollTask;
    private CancellationTokenSource? _watchCancellation;
    private int _disposed;
    private int _watchStarted;

    [SupportedOSPlatform("macos")]
    public MacOSClipboardAdapter(MacOSClipboardOptions? options = null)
        : this(CreateNativeDependencies(options ?? new MacOSClipboardOptions()))
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The macOS clipboard adapter requires macOS.");
        }
    }

    private MacOSClipboardAdapter(NativeDependencies dependencies)
        : this(
            dependencies.Settings,
            dependencies.Pasteboard,
            dependencies.PasteNative,
            dependencies.Delay)
    {
    }

    internal MacOSClipboardAdapter(
        MacOSClipboardSettings settings,
        IMacOSPasteboardNative pasteboard,
        IMacOSPasteNative pasteNative,
        IAsyncDelay delay)
    {
        _settings = settings;
        _pasteboard = pasteboard;
        _delay = delay;
        _eventQueue = new MacOSClipboardEventQueue(settings.EventQueueCapacity);
        _automaticPaste = new MacOSAutomaticPaste(pasteNative, settings, delay);
    }

    public long DroppedEventCount => _eventQueue.DroppedEventCount;

    public bool IsAccessibilityPermissionGranted
    {
        get
        {
            ThrowIfDisposed();
            return _automaticPaste.HasAccessibilityPermission;
        }
    }

    public async IAsyncEnumerable<ClipboardChangedEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _watchStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("A clipboard adapter supports one active watcher.");
        }

        _watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        CancellationToken watchToken = _watchCancellation.Token;

        // 原生 changeCount 读取从后台线程启动。轮询体只做整数比较与有界队列写入，
        // 正文、SQLite 和网络工作全部留给事件消费者。
        _pollTask = Task.Run(() => PollAsync(watchToken), CancellationToken.None);

        try
        {
            await foreach (ClipboardChangedEvent change in
                _eventQueue.ReadAllAsync(watchToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            _watchCancellation.Cancel();
            await AwaitPollCompletionAsync().ConfigureAwait(false);
        }
    }

    public ValueTask<ClipboardReadResult> ReadAsync(
        ClipboardChangedEvent change,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return new ValueTask<ClipboardReadResult>(Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_nativeGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _pasteboard.Read(change);
            }
        }, cancellationToken));
    }

    public ValueTask<ClipboardWriteResult> WriteAsync(
        ClipboardWriteRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        return new ValueTask<ClipboardWriteResult>(Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_nativeGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClipboardWriteResult result = _pasteboard.Write(request);
                if (result.Status is ClipboardWriteStatus.Success or ClipboardWriteStatus.Partial)
                {
                    // 写回和序列登记处于同一临界区，轮询线程不可能先观察到自写 changeCount。
                    _feedbackGuard.Remember(unchecked((long)result.SequenceNumber));
                }

                return result;
            }
        }, cancellationToken));
    }

    public ValueTask<ClipboardWriteResult> WritePlainTextAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteAsync(new ClipboardWriteRequest { Text = text }, cancellationToken);
    }

    public IAutomaticPasteTarget? CaptureForegroundTarget()
    {
        ThrowIfDisposed();
        return _automaticPaste.CaptureForegroundTarget();
    }

    public ValueTask<AutomaticPasteResult> TryPasteAsync(
        IAutomaticPasteTarget target,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _automaticPaste.TryPasteAsync(target, cancellationToken);
    }

    public ValueTask<ForegroundActivationResult> TryActivateTargetAsync(
        IAutomaticPasteTarget target,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _automaticPaste.TryActivateTargetAsync(target, cancellationToken);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        _watchCancellation?.Cancel();
        _eventQueue.Complete();
        await AwaitPollCompletionAsync().ConfigureAwait(false);

        _watchCancellation?.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            long lastChangeCount;
            lock (_nativeGate)
            {
                lastChangeCount = _pasteboard.GetChangeCount();
            }

            MacOSPollingBackoff backoff = new(_settings);
            while (true)
            {
                await _delay.DelayAsync(
                    backoff.CurrentInterval,
                    cancellationToken).ConfigureAwait(false);

                long currentChangeCount;
                lock (_nativeGate)
                {
                    currentChangeCount = _pasteboard.GetChangeCount();
                }

                if (currentChangeCount == lastChangeCount)
                {
                    backoff.RecordUnchanged();
                    continue;
                }

                // changeCount 是 NSInteger，长期运行可能溢出。这里只比较相邻值是否相等，
                // 不用大小关系判断新旧；休眠唤醒后也直接发布当前最新状态。
                lastChangeCount = currentChangeCount;
                backoff.RecordChanged();
                if (_feedbackGuard.TryConsume(currentChangeCount))
                {
                    continue;
                }

                _eventQueue.TryWrite(new ClipboardChangedEvent(
                    MacOSClipboardSequence.ToPublicSequence(currentChangeCount),
                    DateTimeOffset.UtcNow));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            completionError = error;
        }
        finally
        {
            _eventQueue.Complete(completionError);
        }
    }

    private async Task AwaitPollCompletionAsync()
    {
        Task? pollTask = _pollTask;
        if (pollTask is null)
        {
            return;
        }

        try
        {
            await pollTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    [SupportedOSPlatform("macos")]
    private static NativeDependencies CreateNativeDependencies(MacOSClipboardOptions options)
    {
        MacOSClipboardSettings settings = options.ToSettings();
        MacOSClipboardOriginMarker marker = new();
        SystemAsyncDelay delay = new();
        return new NativeDependencies(
            settings,
            new MacOSPasteboardNative(settings, marker),
            new MacOSPasteNative(),
            delay);
    }

    private sealed record NativeDependencies(
        MacOSClipboardSettings Settings,
        IMacOSPasteboardNative Pasteboard,
        IMacOSPasteNative PasteNative,
        IAsyncDelay Delay);
}
