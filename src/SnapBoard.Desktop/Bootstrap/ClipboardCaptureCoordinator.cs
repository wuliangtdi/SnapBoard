using SnapBoard.Application.Clipboard;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.Bootstrap;

internal sealed record ClipboardCaptureState(
    ClipboardCapturePauseReason PauseReasons,
    long ObservedEventCount,
    long ReadCount,
    ClipboardReadStatus? LastReadStatus,
    string? ErrorMessage = null)
{
    public bool IsPaused => PauseReasons != ClipboardCapturePauseReason.None;

    public bool IsManuallyPaused => PauseReasons.HasFlag(ClipboardCapturePauseReason.Manual);

    public bool IsForegroundProtected =>
        PauseReasons.HasFlag(ClipboardCapturePauseReason.ForegroundProtection);
}

[Flags]
internal enum ClipboardCapturePauseReason
{
    None = 0,
    Manual = 1 << 0,
    ForegroundProtection = 1 << 1,
    StorageMigration = 1 << 2,
    UpdateInstallation = 1 << 3,
}

/// <summary>
/// 桌面进程只启动一次剪贴板 WatchAsync。暂停记录时仍持续排空平台有界队列，
/// 但跳过正文读取，防止取消/重启一次性 watcher 导致事件停摆或句柄泄漏。
/// </summary>
internal sealed class ClipboardCaptureCoordinator : IDisposable
{
    private readonly IClipboardCaptureService? _captureService;
    private readonly IDesktopLocalSettingsService? _localSettings;
    private readonly IClipboardMonitor _monitor;
    private readonly IPlatformForegroundWindowStateService? _foregroundWindowStateService;
    private readonly IClipboardContentReader _reader;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _captureTask;
    private long _observedEventCount;
    private long _readCount;
    private int _pauseReasons;
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
        IClipboardCaptureService? captureService,
        IPlatformForegroundWindowStateService? foregroundWindowStateService = null,
        IDesktopLocalSettingsService? localSettings = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(reader);
        _monitor = monitor;
        _reader = reader;
        _captureService = captureService;
        _foregroundWindowStateService = foregroundWindowStateService;
        _localSettings = localSettings;
        if (_localSettings is not null)
        {
            _localSettings.Changed += OnLocalSettingsChanged;
        }
    }

    public event Action<ClipboardCaptureState>? StateChanged;

    public ClipboardCapturePauseReason PauseReasons =>
        (ClipboardCapturePauseReason)Volatile.Read(ref _pauseReasons);

    public bool IsPaused => PauseReasons != ClipboardCapturePauseReason.None;

    public bool IsManuallyPaused =>
        PauseReasons.HasFlag(ClipboardCapturePauseReason.Manual);

    public long ObservedEventCount => Interlocked.Read(ref _observedEventCount);

    public long ReadCount => Interlocked.Read(ref _readCount);

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
        => SetPauseReason(ClipboardCapturePauseReason.Manual, paused);

    public void SetPauseReason(ClipboardCapturePauseReason reason, bool active)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (reason is ClipboardCapturePauseReason.None ||
            !Enum.IsDefined(reason) ||
            !SetPauseReasonCore(reason, active))
        {
            return;
        }

        PublishState(null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_localSettings is not null)
        {
            _localSettings.Changed -= OnLocalSettingsChanged;
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
                bool foregroundProtected = IsForegroundProtectionActive();
                SetPauseReasonCore(
                    ClipboardCapturePauseReason.ForegroundProtection,
                    foregroundProtected);
                if (foregroundProtected || HasPauseReasonOtherThanForegroundProtection())
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
            PauseReasons,
            Interlocked.Read(ref _observedEventCount),
            Interlocked.Read(ref _readCount),
            lastReadStatus,
            errorMessage));
    }

    private bool HasPauseReasonOtherThanForegroundProtection() =>
        (PauseReasons & ~ClipboardCapturePauseReason.ForegroundProtection) !=
        ClipboardCapturePauseReason.None;

    private bool IsForegroundProtectionActive()
    {
        if (_localSettings?.Current.PauseClipboardCaptureWhenProtected != true ||
            _foregroundWindowStateService is null)
        {
            return false;
        }

        try
        {
            return _foregroundWindowStateService.GetForegroundWindowState().IsProtected;
        }
        catch
        {
            // 原生检测失败等同 Unknown，默认放行；异常细节不得进入 UI 或日志。
            return false;
        }
    }

    private bool SetPauseReasonCore(ClipboardCapturePauseReason reason, bool active)
    {
        int reasonValue = (int)reason;
        while (true)
        {
            int current = Volatile.Read(ref _pauseReasons);
            int updated = active ? current | reasonValue : current & ~reasonValue;
            if (updated == current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _pauseReasons, updated, current) == current)
            {
                return true;
            }
        }
    }

    private void OnLocalSettingsChanged(
        object? sender,
        DesktopLocalSettingsChangedEventArgs e)
    {
        if (!e.Settings.PauseClipboardCaptureWhenProtected &&
            SetPauseReasonCore(ClipboardCapturePauseReason.ForegroundProtection, active: false))
        {
            PublishState(null);
        }
    }
}
