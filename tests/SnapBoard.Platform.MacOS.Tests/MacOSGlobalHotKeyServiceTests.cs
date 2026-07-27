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

    [Fact]
    public void KeyMapRejectsMissingModifierAndUnsupportedKey()
    {
        Assert.Equal(
            GlobalHotKeyGestureCreationStatus.MissingModifier,
            MacOSHotKeyKeyMap.CreateGesture(GlobalHotKeyModifiers.None, "V").Status);
        Assert.Equal(
            GlobalHotKeyGestureCreationStatus.UnsupportedKey,
            MacOSHotKeyKeyMap.CreateGesture(GlobalHotKeyModifiers.Meta, "MediaPlayPause").Status);
    }

    [Fact]
    public async Task ConflictRestoresPreviousRegistrationAndConfiguration()
    {
        FakeHotKeyRegistrar registrar = new();
        FakeSettingsStore settings = new();
        await using MacOSGlobalHotKeyService service = new(
            DirectPlatformMainThreadDispatcher.Instance,
            registrar,
            settings);
        GlobalHotKeyGesture original = GlobalHotKeyGesture.MacOSDefault;
        GlobalHotKeyGesture replacement = Assert.IsType<GlobalHotKeyGesture>(
            MacOSHotKeyKeyMap.CreateGesture(
                GlobalHotKeyModifiers.Meta | GlobalHotKeyModifiers.Alt,
                "K").Gesture);

        registrar.Results.Enqueue(0);
        Assert.Equal(
            GlobalHotKeyRegistrationStatus.Registered,
            (await service.RegisterAsync(original, CancellationToken.None)).Status);
        registrar.Results.Enqueue(-9878);
        registrar.Results.Enqueue(0);

        GlobalHotKeyRegistrationResult conflict =
            await service.RegisterAsync(replacement, CancellationToken.None);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Conflict, conflict.Status);
        Assert.Equal(original, registrar.CurrentGesture);
        Assert.Equal(original, service.ConfiguredGesture);
        Assert.Contains(original.DisplayName, settings.Values["GlobalHotKeyV1"], StringComparison.Ordinal);
        Assert.Equal(3, registrar.RegisterCount);
    }

    [Fact]
    public async Task NativeCallbackIsPumpedOutsideRegistrarAndResourcesAreDisposed()
    {
        FakeHotKeyRegistrar registrar = new();
        FakeSettingsStore settings = new();
        await using MacOSGlobalHotKeyService service = new(
            DirectPlatformMainThreadDispatcher.Instance,
            registrar,
            settings);
        TaskCompletionSource pressed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Pressed += (_, _) => pressed.TrySetResult();

        registrar.RaisePressed();

        await pressed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.DisposeAsync();
        Assert.True(registrar.Disposed);
        Assert.True(settings.Disposed);
    }

    private sealed class FakeHotKeyRegistrar : IMacOSHotKeyRegistrar
    {
        public event Action? Pressed;

        public Queue<int> Results { get; } = new();

        public GlobalHotKeyGesture? CurrentGesture { get; private set; }

        public int RegisterCount { get; private set; }

        public bool Disposed { get; private set; }

        public int Register(GlobalHotKeyGesture gesture)
        {
            RegisterCount++;
            int result = Results.Count == 0 ? 0 : Results.Dequeue();
            if (result == 0)
            {
                CurrentGesture = gesture;
            }

            return result;
        }

        public void Unregister() => CurrentGesture = null;

        public void RaisePressed() => Pressed?.Invoke();

        public void Dispose()
        {
            Disposed = true;
            CurrentGesture = null;
        }
    }

    private sealed class FakeSettingsStore : IMacOSSettingsStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public bool Disposed { get; private set; }

        public string? GetString(string key) => Values.GetValueOrDefault(key);

        public void SetString(string key, string value) => Values[key] = value;

        public void Dispose() => Disposed = true;
    }
}
