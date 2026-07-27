using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows.Clipboard;

namespace SnapBoard.Platform.Windows;

/// <summary>
/// Windows 剪贴板能力的聚合适配器。监听、读取、写回和自动粘贴共享同一来源标记
/// 与消息窗口，但 Application 只依赖各自的平台抽象接口。
/// </summary>
public sealed class WindowsClipboardAdapter :
    IClipboardMonitor,
    IClipboardContentReader,
    IClipboardWriter,
    IAutomaticPasteService,
    IDisposable
{
    private readonly object _stopGate = new();
    private readonly IClipboardMessageHost _messageHost;
    private readonly ClipboardEventQueue _eventQueue;
    private readonly ClipboardSequenceDeduplicator _sequenceDeduplicator = new();
    private readonly ClipboardFeedbackGuard _feedbackGuard;
    private readonly WindowsClipboardReader _reader;
    private readonly WindowsClipboardWriter _writer;
    private readonly WindowsAutomaticPaste _automaticPaste;
    private Task? _stopTask;
    private int _disposed;
    private int _watchStarted;

    [SupportedOSPlatform("windows")]
    public WindowsClipboardAdapter(WindowsClipboardOptions? options = null)
        : this(
            (options ?? new WindowsClipboardOptions()).ToSettings(),
            new NativeClipboardMessageHost(),
            new WindowsPasteNative())
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows clipboard adapter requires Windows.");
        }
    }

    internal WindowsClipboardAdapter(
        WindowsClipboardSettings settings,
        IClipboardMessageHost messageHost,
        IWindowsPasteNative pasteNative)
    {
        _messageHost = messageHost;
        _eventQueue = new ClipboardEventQueue(settings.EventQueueCapacity);
        _feedbackGuard = new ClipboardFeedbackGuard();
        ClipboardOriginMarker originMarker = new();
        _reader = new WindowsClipboardReader(settings, originMarker);
        _writer = new WindowsClipboardWriter(settings, originMarker, _feedbackGuard);
        _automaticPaste = new WindowsAutomaticPaste(pasteNative);
        _messageHost.ClipboardUpdated += OnClipboardUpdated;
        _messageHost.MessageLoopStopped += OnMessageLoopStopped;
    }

    public long DroppedEventCount => _eventQueue.DroppedEventCount;

    internal nint MessageWindowHandle => _messageHost.WindowHandle;

    public async IAsyncEnumerable<ClipboardChangedEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _watchStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("A clipboard adapter supports one active watcher.");
        }

        try
        {
            await _messageHost.StartAsync(cancellationToken).ConfigureAwait(false);

            await foreach (ClipboardChangedEvent change in
                _eventQueue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            _eventQueue.Complete();
            await StopMessageHostAsync().ConfigureAwait(false);
        }
    }

    public ValueTask<ClipboardReadResult> ReadAsync(
        ClipboardChangedEvent change,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _reader.ReadAsync(change, cancellationToken);
    }

    public async ValueTask<ClipboardWriteResult> WriteAsync(
        ClipboardWriteRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _messageHost.StartAsync(cancellationToken).ConfigureAwait(false);
        return await _writer.WriteAsync(
                request,
                _messageHost.WindowHandle,
                cancellationToken)
            .ConfigureAwait(false);
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

        _messageHost.ClipboardUpdated -= OnClipboardUpdated;
        _messageHost.MessageLoopStopped -= OnMessageLoopStopped;
        _eventQueue.Complete();
        await StopMessageHostAsync().ConfigureAwait(false);
    }

    private void OnClipboardUpdated(ClipboardUpdateObservation observation)
    {
        if (!_sequenceDeduplicator.TryAccept(observation.SequenceNumber) ||
            _feedbackGuard.TryConsume(observation.SequenceNumber))
        {
            return;
        }

        _eventQueue.TryWrite(new ClipboardChangedEvent(
            observation.SequenceNumber,
            DateTimeOffset.UtcNow,
            new ClipboardSourceProcessHint(
                observation.ClipboardOwnerProcessId,
                observation.ForegroundProcessId)));
    }

    private void OnMessageLoopStopped(Exception? error) => _eventQueue.Complete(error);

    private Task StopMessageHostAsync()
    {
        lock (_stopGate)
        {
            return _stopTask ??= _messageHost.StopAsync().AsTask();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
