using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Desktop;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class WindowsHotKeyRegistrarTests
{
    [Fact]
    public void ConflictRestoresPreviousRegistration()
    {
        FakeWindowsHotKeyNative native = new();
        native.EnqueueRegisterResult(result: true);
        native.EnqueueRegisterResult(
            result: false,
            error: WindowsNativeConstants.ErrorHotKeyAlreadyRegistered);
        native.EnqueueRegisterResult(result: true);
        WindowsHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture replacement = new(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt,
            0x56,
            "Ctrl+Alt+V");

        GlobalHotKeyRegistrationResult first = registrar.Register(123, GlobalHotKeyGesture.Default);
        GlobalHotKeyRegistrationResult second = registrar.Register(123, replacement);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, first.Status);
        Assert.Equal(GlobalHotKeyRegistrationStatus.Conflict, second.Status);
        Assert.Equal(GlobalHotKeyGesture.Default, registrar.CurrentGesture);
        Assert.Equal(3, native.RegisterCount);
        Assert.Equal(1, native.UnregisterCount);
    }

    [Fact]
    public void UnregisterIsIdempotent()
    {
        FakeWindowsHotKeyNative native = new();
        WindowsHotKeyRegistrar registrar = new(native);
        registrar.Register(123, GlobalHotKeyGesture.Default);

        registrar.Unregister(123);
        registrar.Unregister(123);

        Assert.Null(registrar.CurrentGesture);
        Assert.Equal(1, native.UnregisterCount);
    }
}

public sealed class WindowsHotKeyKeyMapTests
{
    [Fact]
    public void CreatesCustomLetterGestureWithCanonicalDisplayName()
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt,
            "K");

        Assert.Equal(GlobalHotKeyGestureCreationStatus.Created, result.Status);
        GlobalHotKeyGesture gesture = Assert.IsType<GlobalHotKeyGesture>(result.Gesture);
        Assert.Equal(0x4Bu, gesture.VirtualKey);
        Assert.Equal(
            GlobalHotKeyModifiers.Control |
            GlobalHotKeyModifiers.Alt |
            GlobalHotKeyModifiers.NoRepeat,
            gesture.Modifiers);
        Assert.Equal("Ctrl+Alt+K", gesture.DisplayName);
    }

    [Theory]
    [InlineData("D7", 0x37u, "Ctrl+7")]
    [InlineData("NumPad3", 0x63u, "Ctrl+Num 3")]
    [InlineData("F24", 0x87u, "Ctrl+F24")]
    [InlineData("OemQuestion", 0xBFu, "Ctrl+/")]
    [InlineData("BrowserBack", 0xA6u, "Ctrl+Browser Back")]
    public void CreatesSupportedWindowsVirtualKeys(
        string keyName,
        uint expectedVirtualKey,
        string expectedDisplayName)
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.Control,
            keyName);

        Assert.Equal(GlobalHotKeyGestureCreationStatus.Created, result.Status);
        Assert.Equal(expectedVirtualKey, result.Gesture?.VirtualKey);
        Assert.Equal(expectedDisplayName, result.Gesture?.DisplayName);
    }

    [Fact]
    public void RejectsGestureWithoutModifier()
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.None,
            "K");

        Assert.Equal(GlobalHotKeyGestureCreationStatus.MissingModifier, result.Status);
        Assert.Null(result.Gesture);
    }

    [Fact]
    public void RejectsModifierAsMainKey()
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.Control,
            "LeftShift");

        Assert.Equal(GlobalHotKeyGestureCreationStatus.UnsupportedKey, result.Status);
        Assert.Null(result.Gesture);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStartServiceTests
{
    [Fact]
    public void EnablesAndDisablesCurrentExecutableCommand()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsAutoStartService service = new(registry, @"C:\Apps\SnapBoard.exe");

        AutoStartUpdateResult enabled = service.SetEnabled(enabled: true);

        Assert.Equal(AutoStartUpdateStatus.Updated, enabled.Status);
        Assert.True(service.IsEnabled());

        AutoStartUpdateResult disabled = service.SetEnabled(enabled: false);

        Assert.Equal(AutoStartUpdateStatus.Updated, disabled.Status);
        Assert.False(service.IsEnabled());
    }
}

[Collection(WindowsClipboardNativeIntegrationTests.CollectionName)]
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopNativeIntegrationTests
{
    [WindowsFact]
    public async Task TwoMessageWindowsReportHotKeyConflictAndReleaseRegistration()
    {
        GlobalHotKeyGesture gesture = new(
            GlobalHotKeyModifiers.Control |
            GlobalHotKeyModifiers.Alt |
            GlobalHotKeyModifiers.Shift |
            GlobalHotKeyModifiers.NoRepeat,
            0x87,
            "Ctrl+Alt+Shift+F24");
        await using WindowsGlobalHotKeyService first = CreateHotKeyService();
        await using WindowsGlobalHotKeyService second = CreateHotKeyService();

        GlobalHotKeyRegistrationResult firstResult =
            await first.RegisterAsync(gesture, CancellationToken.None);
        GlobalHotKeyRegistrationResult secondResult =
            await second.RegisterAsync(gesture, CancellationToken.None);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, firstResult.Status);
        Assert.Equal(GlobalHotKeyRegistrationStatus.Conflict, secondResult.Status);
    }

    [WindowsFact]
    public void SecondInstanceSignalsPrimaryThroughCurrentUserPipe()
    {
        string applicationId = $"SnapBoard.Tests.{Guid.NewGuid():N}";
        Assert.True(WindowsSingleInstanceCoordinator.TryAcquire(
            applicationId,
            SingleInstanceCommand.ActivateMainWindow,
            out WindowsSingleInstanceCoordinator? primary,
            out bool firstNotification));
        Assert.False(firstNotification);
        Assert.NotNull(primary);

        using ManualResetEventSlim commandReceived = new();
        SingleInstanceCommand receivedCommand = default;
        primary.CommandReceived += command =>
        {
            receivedCommand = command;
            commandReceived.Set();
        };
        primary.StartListening();

        Task<(bool Acquired, bool Notified)> secondary = Task.Run(() =>
        {
            bool acquired = WindowsSingleInstanceCoordinator.TryAcquire(
                applicationId,
                SingleInstanceCommand.ShowQuickWindow,
                out WindowsSingleInstanceCoordinator? coordinator,
                out bool notified);
            coordinator?.Dispose();
            return (acquired, notified);
        });

        Assert.True(secondary.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(secondary.Result.Acquired);
        Assert.True(secondary.Result.Notified);
        Assert.True(commandReceived.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(SingleInstanceCommand.ShowQuickWindow, receivedCommand);

        primary.Dispose();
    }

    private static WindowsGlobalHotKeyService CreateHotKeyService() => new(
        new WindowsHotKeyRegistrar(new WindowsHotKeyNative()),
        new FakeWindowsRegistryStore());
}
