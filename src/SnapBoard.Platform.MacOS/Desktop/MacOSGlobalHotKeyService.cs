using System.Globalization;
using System.Runtime.Versioning;
using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOSGlobalHotKeyService : IGlobalHotKeyService, IDisposable
{
    private const int ConflictStatus = -9878;
    private const string HotKeySettingName = "GlobalHotKeyV1";
    private const GlobalHotKeyModifiers KnownModifiers =
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Meta |
        GlobalHotKeyModifiers.NoRepeat;

    private readonly object _gate = new();
    private readonly IPlatformMainThreadDispatcher _dispatcher;
    private readonly IMacOSHotKeyRegistrar _registrar;
    private readonly IMacOSSettingsStore _settings;
    private readonly Channel<bool> _pressedEvents = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly Task _pressedPump;
    private GlobalHotKeyGesture _configuredGesture;
    private int _disposed;

    public MacOSGlobalHotKeyService(IPlatformMainThreadDispatcher dispatcher)
        : this(
            dispatcher,
            dispatcher.Invoke<IMacOSHotKeyRegistrar>(() => new MacOSHotKeyRegistrar()),
            new MacOSSettingsStore())
    {
    }

    internal MacOSGlobalHotKeyService(
        IPlatformMainThreadDispatcher dispatcher,
        IMacOSHotKeyRegistrar registrar,
        IMacOSSettingsStore settings)
    {
        _dispatcher = dispatcher;
        _registrar = registrar;
        _settings = settings;
        _configuredGesture = ReadConfiguredGesture();
        _registrar.Pressed += OnNativePressed;
        _pressedPump = Task.Run(PumpPressedEventsAsync, CancellationToken.None);
    }

    public event EventHandler? Pressed;

    public GlobalHotKeyGesture? CurrentGesture => _registrar.CurrentGesture;

    public GlobalHotKeyGesture ConfiguredGesture
    {
        get
        {
            lock (_gate)
            {
                return _configuredGesture;
            }
        }
    }

    public GlobalHotKeyGesture DefaultGesture => GlobalHotKeyGesture.MacOSDefault;

    public string ModifierDisplayNames => "Command、Option、Control 或 Shift";

    public GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeyModifiers modifiers,
        string keyName) => MacOSHotKeyKeyMap.CreateGesture(modifiers, keyName);

    public async ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!IsValid(gesture))
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Failed);
        }

        GlobalHotKeyRegistrationResult result = await _dispatcher.InvokeAsync(
            () => RegisterOnMainThread(gesture),
            cancellationToken);
        if (result.Status == GlobalHotKeyRegistrationStatus.Registered)
        {
            lock (_gate)
            {
                _configuredGesture = gesture;
            }

            try
            {
                _settings.SetString(HotKeySettingName, SerializeGesture(gesture));
            }
            catch (Exception exception) when (IsSettingsFailure(exception))
            {
                // 会话内注册已成功；偏好写入失败不撤销当前可用快捷键。
            }
        }

        return result;
    }

    public async ValueTask UnregisterAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _registrar.Unregister();
            return true;
        }, cancellationToken);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registrar.Pressed -= OnNativePressed;
        await _dispatcher.InvokeAsync(() =>
        {
            _registrar.Dispose();
            return true;
        });
        _settings.Dispose();
        _pressedEvents.Writer.TryComplete();
        await _pressedPump.ConfigureAwait(false);
    }

    private GlobalHotKeyRegistrationResult RegisterOnMainThread(GlobalHotKeyGesture gesture)
    {
        GlobalHotKeyGesture? previous = _registrar.CurrentGesture;
        if (previous == gesture)
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Registered);
        }

        _registrar.Unregister();
        int status = _registrar.Register(gesture);
        if (status == 0)
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Registered);
        }

        // 新组合键冲突或注册失败时恢复上一组有效快捷键，设置页不会留下失效状态。
        if (previous is GlobalHotKeyGesture previousGesture)
        {
            _registrar.Register(previousGesture);
        }

        return new GlobalHotKeyRegistrationResult(
            status == ConflictStatus
                ? GlobalHotKeyRegistrationStatus.Conflict
                : GlobalHotKeyRegistrationStatus.Failed,
            status);
    }

    private void OnNativePressed() => _pressedEvents.Writer.TryWrite(true);

    private async Task PumpPressedEventsAsync()
    {
        await foreach (bool _ in _pressedEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // 订阅者失败不能终止后续快捷键通知。
            }
        }
    }

    private GlobalHotKeyGesture ReadConfiguredGesture()
    {
        try
        {
            string[] parts = _settings.GetString(HotKeySettingName)?
                .Split('|', 3, StringSplitOptions.TrimEntries) ?? [];
            if (parts.Length == 3 &&
                uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint modifiers) &&
                uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint virtualKey) &&
                virtualKey <= 0x7F &&
                (((GlobalHotKeyModifiers)modifiers) & ~KnownModifiers) == 0 &&
                (((GlobalHotKeyModifiers)modifiers) & ~GlobalHotKeyModifiers.NoRepeat) != 0 &&
                parts[2].Length is > 0 and <= 80)
            {
                return new GlobalHotKeyGesture(
                    (GlobalHotKeyModifiers)modifiers,
                    virtualKey,
                    parts[2]);
            }
        }
        catch (Exception exception) when (IsSettingsFailure(exception))
        {
        }

        return GlobalHotKeyGesture.MacOSDefault;
    }

    private static bool IsValid(GlobalHotKeyGesture gesture) =>
        gesture.VirtualKey <= 0x7F &&
        (gesture.Modifiers & ~KnownModifiers) == 0 &&
        (gesture.Modifiers & ~GlobalHotKeyModifiers.NoRepeat) != 0 &&
        !string.IsNullOrWhiteSpace(gesture.DisplayName);

    private static string SerializeGesture(GlobalHotKeyGesture gesture) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(uint)gesture.Modifiers}|{gesture.VirtualKey}|{gesture.DisplayName}");

    private static bool IsSettingsFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
