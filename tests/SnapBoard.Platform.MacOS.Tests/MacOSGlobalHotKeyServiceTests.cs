using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Desktop;

namespace SnapBoard.Platform.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSGlobalHotKeyServiceTests
{
    [Theory]
    [InlineData("V", 0x09u, "Command+Shift+V")]
    [InlineData("D1", 0x12u, "Command+Shift+1")]
    [InlineData("F12", 0x6Fu, "Command+Shift+F12")]
    [InlineData("OemMinus", 0x1Bu, "Command+Shift+-")]
    public void KeyMapUsesMacKeyCodesAndDisplayNames(
        string keyName,
        uint expectedKeyCode,
        string expectedDisplayName)
    {
        GlobalHotKeyGestureCreationResult result = MacOSHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.Meta | GlobalHotKeyModifiers.Shift,
            keyName);

        GlobalHotKeyGesture gesture = Assert.IsType<GlobalHotKeyGesture>(result.Gesture);
        Assert.Equal(GlobalHotKeyGestureCreationStatus.Created, result.Status);
        Assert.Equal(expectedKeyCode, gesture.VirtualKey);
        Assert.Equal(expectedDisplayName, gesture.DisplayName);
    }

    [Theory]
    [InlineData("K", GlobalHotKeyModifiers.None, 0x28u, "K")]
    [InlineData("LeftCtrl", GlobalHotKeyModifiers.Control, 0x3Bu, "Control")]
    [InlineData("LeftShift", GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Shift, 0x38u, "Control+Shift")]
    [InlineData("RWin", GlobalHotKeyModifiers.Meta, 0x36u, "Right Command")]
    public void KeyMapSupportsPlainAndModifierMainKeys(
        string keyName,
        GlobalHotKeyModifiers modifiers,
        uint expectedKeyCode,
        string expectedDisplayName)
    {
        GlobalHotKeyGestureCreationResult result =
            MacOSHotKeyKeyMap.CreateGesture(modifiers, keyName);

        GlobalHotKeyGesture gesture = Assert.IsType<GlobalHotKeyGesture>(result.Gesture);
        Assert.Equal(expectedKeyCode, gesture.VirtualKey);
        Assert.Equal(modifiers | GlobalHotKeyModifiers.NoRepeat, gesture.Modifiers);
        Assert.Equal(expectedDisplayName, gesture.DisplayName);
    }

    [Fact]
    public void KeyMapRejectsUnsupportedKey()
    {
        Assert.Equal(
            GlobalHotKeyGestureCreationStatus.UnsupportedKey,
            MacOSHotKeyKeyMap.CreateGesture(
                GlobalHotKeyModifiers.Meta,
                "MediaPlayPause").Status);
    }

    [Fact]
    public async Task RegistersBothSlotsPersistsThemAndRejectsDuplicateBinding()
    {
        FakeHotKeyRegistrar registrar = new();
        FakeDesktopLocalSettingsService settings = new();
        await using MacOSGlobalHotKeyService service = new(
            DirectPlatformMainThreadDispatcher.Instance,
            registrar,
            settings,
            TimeSpan.FromMilliseconds(550));
        GlobalHotKeyGesture doubleGesture = CreateGesture(0x28, "K");

        GlobalHotKeyRegistrationResult primary = await service.RegisterAsync(
            GlobalHotKeySlot.Primary,
            GlobalHotKeyGesture.MacOSDefault,
            CancellationToken.None);
        GlobalHotKeyRegistrationResult doubleResult = await service.RegisterAsync(
            GlobalHotKeySlot.Double,
            doubleGesture,
            CancellationToken.None);
        GlobalHotKeyRegistrationResult duplicate = await service.RegisterAsync(
            GlobalHotKeySlot.Double,
            GlobalHotKeyGesture.MacOSDefault with { DisplayName = "duplicate" },
            CancellationToken.None);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, primary.Status);
        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, doubleResult.Status);
        Assert.Equal(GlobalHotKeyRegistrationStatus.Duplicate, duplicate.Status);
        Assert.Equal(GlobalHotKeyGesture.MacOSDefault, settings.Current.PrimaryHotKey);
        Assert.Equal(doubleGesture, settings.Current.DoubleHotKey);
        Assert.Equal(TimeSpan.FromMilliseconds(550), service.DoubleTriggerInterval);
        Assert.Equal(2, registrar.RegisterCount);
    }

    [Fact]
    public async Task ConflictPreservesPreviousRegistrationAndConfiguration()
    {
        FakeHotKeyRegistrar registrar = new();
        FakeDesktopLocalSettingsService settings = new();
        await using MacOSGlobalHotKeyService service = new(
            DirectPlatformMainThreadDispatcher.Instance,
            registrar,
            settings);
        GlobalHotKeyGesture original = CreateGesture(0x28, "K");
        GlobalHotKeyGesture replacement = CreateGesture(0x25, "L");
        await service.RegisterAsync(
            GlobalHotKeySlot.Double,
            original,
            CancellationToken.None);
        registrar.Results.Enqueue(new GlobalHotKeyRegistrationResult(
            GlobalHotKeyRegistrationStatus.Conflict,
            -9878));

        GlobalHotKeyRegistrationResult conflict = await service.RegisterAsync(
            GlobalHotKeySlot.Double,
            replacement,
            CancellationToken.None);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Conflict, conflict.Status);
        Assert.Equal(original, registrar.GetCurrentGesture(GlobalHotKeySlot.Double));
        Assert.Equal(original, settings.Current.DoubleHotKey);
    }

    [Fact]
    public async Task ClearingDoubleKeepsPrimaryAndReportsSettingsFailure()
    {
        FakeHotKeyRegistrar registrar = new();
        FakeDesktopLocalSettingsService settings = new();
        await using MacOSGlobalHotKeyService service = new(
            DirectPlatformMainThreadDispatcher.Instance,
            registrar,
            settings);
        GlobalHotKeyGesture doubleGesture = CreateGesture(0x28, "K");
        await service.RegisterAsync(
            GlobalHotKeySlot.Primary,
            GlobalHotKeyGesture.MacOSDefault,
            CancellationToken.None);
        await service.RegisterAsync(
            GlobalHotKeySlot.Double,
            doubleGesture,
            CancellationToken.None);
        settings.Persisted = false;

        GlobalHotKeyRegistrationResult result = await service.ClearAsync(
            GlobalHotKeySlot.Double,
            CancellationToken.None);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, result.Status);
        Assert.False(result.SettingsPersisted);
        Assert.Equal(
            GlobalHotKeyGesture.MacOSDefault,
            registrar.GetCurrentGesture(GlobalHotKeySlot.Primary));
        Assert.Null(registrar.GetCurrentGesture(GlobalHotKeySlot.Double));
        Assert.Null(settings.Current.DoubleHotKey);
    }

    [Fact]
    public async Task NativeCallbacksKeepSourceAndRepeatOutsideRegistrar()
    {
        FakeHotKeyRegistrar registrar = new();
        FakeDesktopLocalSettingsService settings = new();
        await using MacOSGlobalHotKeyService service = new(
            DirectPlatformMainThreadDispatcher.Instance,
            registrar,
            settings);
        TaskCompletionSource<GlobalHotKeyTriggeredEventArgs> triggered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Triggered += (_, args) => triggered.TrySetResult(args);

        registrar.RaiseTriggered(GlobalHotKeySlot.Double, isRepeat: true);

        GlobalHotKeyTriggeredEventArgs result =
            await triggered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(GlobalHotKeySlot.Double, result.Source);
        Assert.True(result.IsRepeat);
        await service.DisposeAsync();
        Assert.True(registrar.Disposed);
        Assert.Equal(0, settings.DisposeCount);
    }

    private static GlobalHotKeyGesture CreateGesture(uint virtualKey, string displayName) => new(
        GlobalHotKeyModifiers.NoRepeat,
        virtualKey,
        displayName);

    private sealed class FakeHotKeyRegistrar : IMacOSHotKeyRegistrar
    {
        private readonly Dictionary<GlobalHotKeySlot, GlobalHotKeyGesture> _gestures = [];

        public event Action<MacOSHotKeyNativeEvent>? Triggered;

        public Queue<GlobalHotKeyRegistrationResult> Results { get; } = new();

        public int RegisterCount { get; private set; }

        public bool Disposed { get; private set; }

        public GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot) =>
            _gestures.TryGetValue(slot, out GlobalHotKeyGesture gesture)
                ? gesture
                : null;

        public GlobalHotKeyRegistrationResult Register(
            GlobalHotKeySlot slot,
            GlobalHotKeyGesture gesture)
        {
            RegisterCount++;
            GlobalHotKeyRegistrationResult result = Results.Count == 0
                ? new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Registered)
                : Results.Dequeue();
            if (result.Status == GlobalHotKeyRegistrationStatus.Registered)
            {
                _gestures[slot] = gesture;
            }

            return result;
        }

        public GlobalHotKeyRegistrationResult Clear(GlobalHotKeySlot slot)
        {
            _gestures.Remove(slot);
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Registered);
        }

        public void UnregisterAll() => _gestures.Clear();

        public void RaiseTriggered(GlobalHotKeySlot slot, bool isRepeat) =>
            Triggered?.Invoke(new MacOSHotKeyNativeEvent(slot, isRepeat));

        public void Dispose()
        {
            Disposed = true;
            _gestures.Clear();
        }
    }

    private sealed class FakeDesktopLocalSettingsService : IDesktopLocalSettingsService
    {
        public event EventHandler<DesktopLocalSettingsChangedEventArgs>? Changed;

        public DesktopLocalSettings Current { get; private set; } =
            DesktopLocalSettings.CreateDefaults(GlobalHotKeyGesture.MacOSDefault);

        public bool Persisted { get; set; } = true;

        public int DisposeCount { get; private set; }

        public DesktopLocalSettingsUpdateResult Update(DesktopLocalSettings settings) =>
            Update(_ => settings);

        public DesktopLocalSettingsUpdateResult Update(
            Func<DesktopLocalSettings, DesktopLocalSettings> update)
        {
            Current = update(Current);
            Changed?.Invoke(this, new DesktopLocalSettingsChangedEventArgs(Current));
            return new DesktopLocalSettingsUpdateResult(Persisted);
        }
    }
}
