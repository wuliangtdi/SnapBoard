using System.Runtime.Versioning;
using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOSGlobalHotKeyService :
    IGlobalHotKeyService,
    ITwoSlotGlobalHotKeyService,
    IDisposable
{
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly IPlatformMainThreadDispatcher _dispatcher;
    private readonly IMacOSHotKeyRegistrar _registrar;
    private readonly IDesktopLocalSettingsService _settings;
    private readonly Channel<MacOSHotKeyNativeEvent> _triggeredEvents =
        Channel.CreateBounded<MacOSHotKeyNativeEvent>(
            new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true,
            });
    private readonly Task _triggeredPump;
    private readonly TimeSpan _doubleTriggerInterval;
    private int _disposed;

    public MacOSGlobalHotKeyService(
        IPlatformMainThreadDispatcher dispatcher,
        MacOSDesktopLocalSettingsService settings)
        : this(
            dispatcher,
            dispatcher.Invoke<IMacOSHotKeyRegistrar>(() => new MacOSHotKeyRegistrar()),
            settings,
            GetSystemDoubleTriggerInterval(dispatcher))
    {
    }

    internal MacOSGlobalHotKeyService(
        IPlatformMainThreadDispatcher dispatcher,
        IMacOSHotKeyRegistrar registrar,
        IDesktopLocalSettingsService settings,
        TimeSpan? doubleTriggerInterval = null)
    {
        _dispatcher = dispatcher;
        _registrar = registrar;
        _settings = settings;
        _doubleTriggerInterval = doubleTriggerInterval ?? TimeSpan.FromMilliseconds(400);
        _registrar.Triggered += OnNativeTriggered;
        _triggeredPump = Task.Run(PumpTriggeredEventsAsync, CancellationToken.None);
    }

    public event EventHandler? Pressed;

    public event EventHandler<GlobalHotKeyTriggeredEventArgs>? Triggered;

    public GlobalHotKeyGesture? CurrentGesture =>
        _registrar.GetCurrentGesture(GlobalHotKeySlot.Primary);

    public GlobalHotKeyGesture ConfiguredGesture => _settings.Current.PrimaryHotKey;

    public GlobalHotKeyGesture DefaultGesture => GlobalHotKeyGesture.MacOSDefault;

    public string ModifierDisplayNames => "Command、Option、Control 或 Shift";

    public TimeSpan DoubleTriggerInterval => _doubleTriggerInterval;

    public GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeyModifiers modifiers,
        string keyName) => MacOSHotKeyKeyMap.CreateGesture(modifiers, keyName);

    public GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeySlot slot,
        GlobalHotKeyModifiers modifiers,
        string keyName) => Enum.IsDefined(slot)
        ? MacOSHotKeyKeyMap.CreateGesture(modifiers, keyName)
        : new GlobalHotKeyGestureCreationResult(
            GlobalHotKeyGestureCreationStatus.UnsupportedKey);

    public async ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken) =>
        await RegisterAsync(GlobalHotKeySlot.Primary, gesture, cancellationToken)
            .ConfigureAwait(false);

    public GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot) =>
        _registrar.GetCurrentGesture(slot);

    public GlobalHotKeyGesture? GetConfiguredGesture(GlobalHotKeySlot slot) => slot switch
    {
        GlobalHotKeySlot.Primary => _settings.Current.PrimaryHotKey,
        GlobalHotKeySlot.Double => _settings.Current.DoubleHotKey,
        _ => null,
    };

    public async ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeySlot slot,
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Enum.IsDefined(slot) ||
                !MacOSDesktopLocalSettingsService.IsValidGesture(gesture))
            {
                return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Failed);
            }

            DesktopLocalSettings current = _settings.Current;
            GlobalHotKeyGesture? otherGesture = slot == GlobalHotKeySlot.Primary
                ? current.DoubleHotKey
                : current.PrimaryHotKey;
            if (otherGesture is GlobalHotKeyGesture other && gesture.HasSameBinding(other))
            {
                return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Duplicate);
            }

            GlobalHotKeyRegistrationResult result = await _dispatcher.InvokeAsync(
                () => _registrar.Register(slot, gesture),
                cancellationToken);
            if (result.Status != GlobalHotKeyRegistrationStatus.Registered ||
                _registrar.GetCurrentGesture(slot) != gesture)
            {
                return result;
            }

            DesktopLocalSettingsUpdateResult updateResult = _settings.Update(settings =>
                slot == GlobalHotKeySlot.Primary
                    ? settings with { PrimaryHotKey = gesture }
                    : settings with { DoubleHotKey = gesture });
            return result with { SettingsPersisted = updateResult.Persisted };
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async ValueTask<GlobalHotKeyRegistrationResult> ClearAsync(
        GlobalHotKeySlot slot,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (slot != GlobalHotKeySlot.Double)
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Unsupported);
        }

        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GlobalHotKeyRegistrationResult result = await _dispatcher.InvokeAsync(
                () => _registrar.Clear(slot),
                cancellationToken);
            if (result.Status != GlobalHotKeyRegistrationStatus.Registered)
            {
                return result;
            }

            DesktopLocalSettingsUpdateResult updateResult =
                _settings.Update(settings => settings with { DoubleHotKey = null });
            return result with { SettingsPersisted = updateResult.Persisted };
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async ValueTask UnregisterAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _registrar.UnregisterAll();
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

        _registrar.Triggered -= OnNativeTriggered;
        await _dispatcher.InvokeAsync(() =>
        {
            _registrar.Dispose();
            return true;
        });
        _triggeredEvents.Writer.TryComplete();
        await _triggeredPump.ConfigureAwait(false);
        _configurationGate.Dispose();
    }

    private void OnNativeTriggered(MacOSHotKeyNativeEvent trigger) =>
        _triggeredEvents.Writer.TryWrite(trigger);

    private async Task PumpTriggeredEventsAsync()
    {
        await foreach (MacOSHotKeyNativeEvent trigger in
            _triggeredEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Triggered?.Invoke(
                    this,
                    new GlobalHotKeyTriggeredEventArgs(trigger.Source, trigger.IsRepeat));
                if (trigger.Source == GlobalHotKeySlot.Primary && !trigger.IsRepeat)
                {
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
            }
            catch
            {
                // 订阅者失败不能终止后续快捷键通知。
            }
        }
    }

    private static TimeSpan GetSystemDoubleTriggerInterval(
        IPlatformMainThreadDispatcher dispatcher)
    {
        try
        {
            double seconds = dispatcher.Invoke(() => MacOSNativeMethods.SendDouble(
                ObjectiveC.GetRequiredClass("NSEvent"),
                ObjectiveC.GetSelector("doubleClickInterval")));
            if (double.IsFinite(seconds) && seconds > 0)
            {
                return TimeSpan.FromMilliseconds(
                    Math.Clamp(seconds * 1000d, 250d, 700d));
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or EntryPointNotFoundException)
        {
        }

        return TimeSpan.FromMilliseconds(400);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
