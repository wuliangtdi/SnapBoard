using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Desktop;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class WindowsHotKeyRegistrarTests
{
    [Fact]
    public void DoubleSlotConflictPreservesBothRegistrations()
    {
        FakeWindowsHotKeyNative native = new();
        WindowsHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture doubleGesture = CreateGesture(0x4B, "Ctrl+Alt+K");
        GlobalHotKeyGesture replacement = CreateGesture(0x4C, "Ctrl+Alt+L");

        Assert.Equal(
            GlobalHotKeyRegistrationStatus.Registered,
            registrar.Register(
                123,
                GlobalHotKeySlot.Primary,
                GlobalHotKeyGesture.WindowsDefault).Status);
        Assert.Equal(
            GlobalHotKeyRegistrationStatus.Registered,
            registrar.Register(123, GlobalHotKeySlot.Double, doubleGesture).Status);
        int? primaryIdentifier = registrar.GetCurrentIdentifier(GlobalHotKeySlot.Primary);
        int? doubleIdentifier = registrar.GetCurrentIdentifier(GlobalHotKeySlot.Double);
        native.EnqueueRegisterResult(
            result: false,
            error: WindowsNativeConstants.ErrorHotKeyAlreadyRegistered);

        GlobalHotKeyRegistrationResult result = registrar.Register(
            123,
            GlobalHotKeySlot.Double,
            replacement);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Conflict, result.Status);
        Assert.Equal(
            GlobalHotKeyGesture.WindowsDefault,
            registrar.GetCurrentGesture(GlobalHotKeySlot.Primary));
        Assert.Equal(doubleGesture, registrar.GetCurrentGesture(GlobalHotKeySlot.Double));
        Assert.Equal(primaryIdentifier, registrar.GetCurrentIdentifier(GlobalHotKeySlot.Primary));
        Assert.Equal(doubleIdentifier, registrar.GetCurrentIdentifier(GlobalHotKeySlot.Double));
        Assert.Equal(3, native.RegisterCount);
        Assert.Equal(0, native.UnregisterCount);
    }

    [Fact]
    public void RegistersTwoDistinctIdsWithNoRepeatAndMapsTheirSources()
    {
        FakeWindowsHotKeyNative native = new();
        WindowsHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture doubleGesture = new(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.NoRepeat,
            0x11,
            "Ctrl");

        registrar.Register(123, GlobalHotKeySlot.Primary, GlobalHotKeyGesture.WindowsDefault);
        registrar.Register(123, GlobalHotKeySlot.Double, doubleGesture);

        Assert.Collection(
            native.Registrations,
            primary =>
            {
                Assert.Equal(WindowsHotKeyRegistrar.PrimaryRegistrationIdentifier, primary.Identifier);
                Assert.True((((GlobalHotKeyModifiers)primary.Modifiers) &
                    GlobalHotKeyModifiers.NoRepeat) != 0);
            },
            doubleRegistration =>
            {
                Assert.Equal(
                    WindowsHotKeyRegistrar.DoubleRegistrationIdentifier,
                    doubleRegistration.Identifier);
                Assert.Equal(
                    GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.NoRepeat,
                    (GlobalHotKeyModifiers)doubleRegistration.Modifiers);
                Assert.Equal(0x11u, doubleRegistration.VirtualKey);
            });
        Assert.True(WindowsHotKeyRegistrar.TryGetSlot(
            WindowsHotKeyRegistrar.PrimaryRegistrationIdentifier,
            out GlobalHotKeySlot primarySource));
        Assert.Equal(GlobalHotKeySlot.Primary, primarySource);
        Assert.True(WindowsHotKeyRegistrar.TryGetSlot(
            WindowsHotKeyRegistrar.DoubleRegistrationIdentifier,
            out GlobalHotKeySlot doubleSource));
        Assert.Equal(GlobalHotKeySlot.Double, doubleSource);
    }

    [Fact]
    public void DuplicateGestureIsRejectedBeforeNativeRegistration()
    {
        FakeWindowsHotKeyNative native = new();
        WindowsHotKeyRegistrar registrar = new(native);
        registrar.Register(123, GlobalHotKeySlot.Primary, GlobalHotKeyGesture.WindowsDefault);

        GlobalHotKeyRegistrationResult result = registrar.Register(
            123,
            GlobalHotKeySlot.Double,
            GlobalHotKeyGesture.WindowsDefault);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Duplicate, result.Status);
        Assert.Equal(1, native.RegisterCount);
        Assert.Null(registrar.GetCurrentGesture(GlobalHotKeySlot.Double));
    }

    [Fact]
    public void DuplicateNativeBindingIsRejectedEvenWhenDisplayNamesDiffer()
    {
        FakeWindowsHotKeyNative native = new();
        WindowsHotKeyRegistrar registrar = new(native);
        registrar.Register(123, GlobalHotKeySlot.Primary, GlobalHotKeyGesture.WindowsDefault);
        GlobalHotKeyGesture disguisedDuplicate = GlobalHotKeyGesture.WindowsDefault with
        {
            DisplayName = "different-display-name",
        };

        GlobalHotKeyRegistrationResult result = registrar.Register(
            123,
            GlobalHotKeySlot.Double,
            disguisedDuplicate);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Duplicate, result.Status);
        Assert.Equal(1, native.RegisterCount);
    }

    [Fact]
    public void ClearingDoubleSlotDoesNotUnregisterPrimary()
    {
        FakeWindowsHotKeyNative native = new();
        WindowsHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture doubleGesture = CreateGesture(0x4B, "Ctrl+Alt+K");
        registrar.Register(123, GlobalHotKeySlot.Primary, GlobalHotKeyGesture.WindowsDefault);
        registrar.Register(123, GlobalHotKeySlot.Double, doubleGesture);

        registrar.Clear(123, GlobalHotKeySlot.Double);
        registrar.Clear(123, GlobalHotKeySlot.Double);

        Assert.Equal(
            GlobalHotKeyGesture.WindowsDefault,
            registrar.GetCurrentGesture(GlobalHotKeySlot.Primary));
        Assert.Null(registrar.GetCurrentGesture(GlobalHotKeySlot.Double));
        Assert.Equal(
            [WindowsHotKeyRegistrar.DoubleRegistrationIdentifier],
            native.UnregisteredIdentifiers);
    }

    [Fact]
    public void ClearedOrReplacedIdentifiersNoLongerMapToActiveSources()
    {
        FakeWindowsHotKeyNative native = new();
        WindowsHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture first = CreateGesture(0x4B, "Ctrl+Alt+K");
        GlobalHotKeyGesture replacement = CreateGesture(0x4C, "Ctrl+Alt+L");
        registrar.Register(123, GlobalHotKeySlot.Double, first);
        int firstIdentifier = Assert.IsType<int>(
            registrar.GetCurrentIdentifier(GlobalHotKeySlot.Double));

        registrar.Register(123, GlobalHotKeySlot.Double, replacement);
        int replacementIdentifier = Assert.IsType<int>(
            registrar.GetCurrentIdentifier(GlobalHotKeySlot.Double));

        Assert.False(registrar.TryGetActiveSlot(firstIdentifier, out _));
        Assert.True(registrar.TryGetActiveSlot(
            replacementIdentifier,
            out GlobalHotKeySlot replacementSlot));
        Assert.Equal(GlobalHotKeySlot.Double, replacementSlot);

        registrar.Clear(123, GlobalHotKeySlot.Double);

        Assert.False(registrar.TryGetActiveSlot(replacementIdentifier, out _));
    }

    [Fact]
    public void FailedOldIdReleaseRollsBackNewRegistration()
    {
        FakeWindowsHotKeyNative native = new();
        WindowsHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture original = CreateGesture(0x4B, "Ctrl+Alt+K");
        GlobalHotKeyGesture replacement = CreateGesture(0x4C, "Ctrl+Alt+L");
        registrar.Register(123, GlobalHotKeySlot.Double, original);
        native.EnqueueUnregisterResult(result: false, error: 5);

        GlobalHotKeyRegistrationResult result = registrar.Register(
            123,
            GlobalHotKeySlot.Double,
            replacement);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Failed, result.Status);
        Assert.Equal(original, registrar.GetCurrentGesture(GlobalHotKeySlot.Double));
        Assert.Equal(
            [
                WindowsHotKeyRegistrar.DoubleRegistrationIdentifier,
                WindowsHotKeyRegistrar.AlternateDoubleRegistrationIdentifier,
            ],
            native.UnregisteredIdentifiers);
    }

    private static GlobalHotKeyGesture CreateGesture(uint virtualKey, string displayName) => new(
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.NoRepeat,
        virtualKey,
        displayName);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopLocalSettingsServiceTests
{
    [Fact]
    public void FreshInstallUsesAndPersistsCurrentDefaults()
    {
        FakeWindowsRegistryStore registry = new();

        WindowsDesktopLocalSettingsService service = new(registry);

        Assert.Equal(GlobalHotKeyGesture.WindowsDefault, service.Current.PrimaryHotKey);
        Assert.Null(service.Current.DoubleHotKey);
        Assert.Equal(
            ForegroundProtectionScope.FullScreenOnly,
            service.Current.ProtectionScope);
        Assert.True(service.Current.DisableGlobalHotKeysWhenProtected);
        Assert.True(service.Current.PauseClipboardCaptureWhenProtected);
        Assert.True(registry.TryGetValue(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.VersionValueName,
            out string? version));
        Assert.Equal(WindowsDesktopLocalSettingsService.CurrentVersion, version);
    }

    [Fact]
    public void TwoGesturesAndProtectionSettingsSurviveRestart()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsDesktopLocalSettingsService first = new(registry);
        GlobalHotKeyGesture primary = new(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.NoRepeat,
            0x11,
            "Ctrl");
        GlobalHotKeyGesture doubleGesture = new(
            GlobalHotKeyModifiers.NoRepeat,
            0x4B,
            "K");
        DesktopLocalSettings expected = new(
            primary,
            doubleGesture,
            ProtectionScope: ForegroundProtectionScope.FullScreenAndMaximized,
            DisableGlobalHotKeysWhenProtected: false,
            PauseClipboardCaptureWhenProtected: false);

        DesktopLocalSettingsUpdateResult update = first.Update(expected);
        WindowsDesktopLocalSettingsService restarted = new(registry);

        Assert.True(update.Persisted);
        Assert.Equal(expected, restarted.Current);
    }

    [Fact]
    public void ModifierOnlyGesturesSurviveRestartWithRequiredRegistrationFlags()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsDesktopLocalSettingsService first = new(registry);
        GlobalHotKeyGesture primary = new(
            GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
            0x12,
            "Alt");
        GlobalHotKeyGesture doubleGesture = new(
            GlobalHotKeyModifiers.Control |
            GlobalHotKeyModifiers.Shift |
            GlobalHotKeyModifiers.NoRepeat,
            0x10,
            "Ctrl+Shift");

        DesktopLocalSettingsUpdateResult update = first.Update(
            first.Current with
            {
                PrimaryHotKey = primary,
                DoubleHotKey = doubleGesture,
            });
        WindowsDesktopLocalSettingsService restarted = new(registry);

        Assert.True(update.Persisted);
        Assert.Equal(primary, restarted.Current.PrimaryHotKey);
        Assert.Equal(doubleGesture, restarted.Current.DoubleHotKey);
    }

    [Fact]
    public void MissingCurrentVersionIgnoresDevelopmentHotKeyValue()
    {
        FakeWindowsRegistryStore registry = new();
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            "GlobalHotKey",
            "legacy-development-value");

        WindowsDesktopLocalSettingsService service = new(registry);

        Assert.Equal(GlobalHotKeyGesture.WindowsDefault, service.Current.PrimaryHotKey);
        Assert.Null(service.Current.DoubleHotKey);
    }

    [Fact]
    public void PreviousFormatVersionIsRejectedWithoutMigration()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsDesktopLocalSettingsService previous = new(registry);
        previous.Update(previous.Current with
        {
            PrimaryHotKey = CreateGesture(0x4A, "Ctrl+Alt+J"),
            DoubleHotKey = CreateGesture(0x4B, "Ctrl+Alt+K"),
            ProtectionScope = ForegroundProtectionScope.FullScreenAndMaximized,
            DisableGlobalHotKeysWhenProtected = false,
            PauseClipboardCaptureWhenProtected = false,
        });
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.VersionValueName,
            "2");

        WindowsDesktopLocalSettingsService current = new(registry);

        Assert.Equal("3", WindowsDesktopLocalSettingsService.CurrentVersion);
        Assert.Equal(GlobalHotKeyGesture.WindowsDefault, current.Current.PrimaryHotKey);
        Assert.Null(current.Current.DoubleHotKey);
        Assert.Equal(
            ForegroundProtectionScope.FullScreenOnly,
            current.Current.ProtectionScope);
        Assert.True(current.Current.DisableGlobalHotKeysWhenProtected);
        Assert.True(current.Current.PauseClipboardCaptureWhenProtected);
    }

    [Fact]
    public void InvalidCurrentFieldRejectsWholeConfiguration()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsDesktopLocalSettingsService valid = new(registry);
        valid.Update(new DesktopLocalSettings(
            CreateGesture(0x4A, "Ctrl+Alt+J"),
            CreateGesture(0x4B, "Ctrl+Alt+K"),
            ProtectionScope: ForegroundProtectionScope.FullScreenAndMaximized,
            DisableGlobalHotKeysWhenProtected: false,
            PauseClipboardCaptureWhenProtected: false));
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.PauseCaptureValueName,
            "invalid");

        WindowsDesktopLocalSettingsService restarted = new(registry);

        Assert.Equal(GlobalHotKeyGesture.WindowsDefault, restarted.Current.PrimaryHotKey);
        Assert.Null(restarted.Current.DoubleHotKey);
        Assert.Equal(
            ForegroundProtectionScope.FullScreenOnly,
            restarted.Current.ProtectionScope);
        Assert.True(restarted.Current.DisableGlobalHotKeysWhenProtected);
        Assert.True(restarted.Current.PauseClipboardCaptureWhenProtected);
    }

    [Fact]
    public void ModifierMainKeyWithoutItsRequiredFlagIsRejected()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsDesktopLocalSettingsService service = new(registry);
        GlobalHotKeyGesture invalid = new(
            GlobalHotKeyModifiers.NoRepeat,
            0x12,
            "Alt");

        Assert.False(WindowsDesktopLocalSettingsService.IsValidGesture(invalid));
        Assert.Throws<ArgumentException>(() => service.Update(
            service.Current with { DoubleHotKey = invalid }));
    }

    [Fact]
    public void CurrentFormatRejectsDuplicateBindingsWithDifferentDisplayNames()
    {
        FakeWindowsRegistryStore registry = new();
        GlobalHotKeyGesture primary = CreateGesture(0x4A, "Ctrl+Alt+J");
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.ProtectionScopeValueName,
            ((int)ForegroundProtectionScope.FullScreenAndMaximized).ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.VersionValueName,
            WindowsDesktopLocalSettingsService.CurrentVersion);
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.PrimaryHotKeyValueName,
            $"{(uint)primary.Modifiers}|{primary.VirtualKey}|{primary.DisplayName}");
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.DoubleHotKeyValueName,
            $"{(uint)primary.Modifiers}|{primary.VirtualKey}|different-display");
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.DisableHotKeysValueName,
            "0");
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.PauseCaptureValueName,
            "0");

        WindowsDesktopLocalSettingsService service = new(registry);

        Assert.Equal(GlobalHotKeyGesture.WindowsDefault, service.Current.PrimaryHotKey);
        Assert.Null(service.Current.DoubleHotKey);
        Assert.Equal(
            ForegroundProtectionScope.FullScreenOnly,
            service.Current.ProtectionScope);
        Assert.True(service.Current.DisableGlobalHotKeysWhenProtected);
        Assert.True(service.Current.PauseClipboardCaptureWhenProtected);
    }

    [Fact]
    public void InvalidProtectionScopeRejectsWholeConfiguration()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsDesktopLocalSettingsService valid = new(registry);
        valid.Update(valid.Current with
        {
            PrimaryHotKey = CreateGesture(0x4A, "Ctrl+Alt+J"),
            ProtectionScope = ForegroundProtectionScope.FullScreenAndMaximized,
        });
        registry.Seed(
            WindowsDesktopLocalSettingsService.SettingsSubKey,
            WindowsDesktopLocalSettingsService.ProtectionScopeValueName,
            "9");

        WindowsDesktopLocalSettingsService restarted = new(registry);

        Assert.Equal(GlobalHotKeyGesture.WindowsDefault, restarted.Current.PrimaryHotKey);
        Assert.Equal(
            ForegroundProtectionScope.FullScreenOnly,
            restarted.Current.ProtectionScope);
    }

    private static GlobalHotKeyGesture CreateGesture(uint virtualKey, string displayName) => new(
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.NoRepeat,
        virtualKey,
        displayName);
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
    public void CreatesModifierlessGestureWithNoRepeat()
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.None,
            "K");

        Assert.Equal(GlobalHotKeyGestureCreationStatus.Created, result.Status);
        GlobalHotKeyGesture gesture = Assert.IsType<GlobalHotKeyGesture>(result.Gesture);
        Assert.Equal(0x4Bu, gesture.VirtualKey);
        Assert.Equal(GlobalHotKeyModifiers.NoRepeat, gesture.Modifiers);
        Assert.Equal("K", gesture.DisplayName);
    }

    [Theory]
    [InlineData("LeftCtrl", 0x11u, "Ctrl", GlobalHotKeyModifiers.Control)]
    [InlineData("RightCtrl", 0x11u, "Ctrl", GlobalHotKeyModifiers.Control)]
    [InlineData("LeftAlt", 0x12u, "Alt", GlobalHotKeyModifiers.Alt)]
    [InlineData("RightAlt", 0x12u, "Alt", GlobalHotKeyModifiers.Alt)]
    [InlineData("LeftShift", 0x10u, "Shift", GlobalHotKeyModifiers.Shift)]
    [InlineData("RightShift", 0x10u, "Shift", GlobalHotKeyModifiers.Shift)]
    [InlineData("LWin", 0x5Bu, "Win", GlobalHotKeyModifiers.Windows)]
    [InlineData("RWin", 0x5Cu, "Right Win", GlobalHotKeyModifiers.Windows)]
    public void CreatesSingleModifierGesture(
        string keyName,
        uint expectedVirtualKey,
        string expectedDisplayName,
        GlobalHotKeyModifiers expectedModifier)
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.None,
            keyName);

        Assert.Equal(GlobalHotKeyGestureCreationStatus.Created, result.Status);
        GlobalHotKeyGesture gesture = Assert.IsType<GlobalHotKeyGesture>(result.Gesture);
        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(
            expectedModifier | GlobalHotKeyModifiers.NoRepeat,
            gesture.Modifiers);
        Assert.Equal(expectedDisplayName, gesture.DisplayName);
    }

    [Fact]
    public void MainModifierIsKeptForRegistrationAndRemovedOnlyFromDisplay()
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.Control,
            "LeftCtrl");

        Assert.Equal(GlobalHotKeyGestureCreationStatus.Created, result.Status);
        Assert.Equal(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.NoRepeat,
            result.Gesture?.Modifiers);
        Assert.Equal("Ctrl", result.Gesture?.DisplayName);
    }

    [Fact]
    public void CreatesModifierOnlyChordUsingLastModifierAsMainKey()
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Shift,
            "LeftShift");

        Assert.Equal(GlobalHotKeyGestureCreationStatus.Created, result.Status);
        GlobalHotKeyGesture gesture = Assert.IsType<GlobalHotKeyGesture>(result.Gesture);
        Assert.Equal(0x10u, gesture.VirtualKey);
        Assert.Equal(
            GlobalHotKeyModifiers.Control |
            GlobalHotKeyModifiers.Shift |
            GlobalHotKeyModifiers.NoRepeat,
            gesture.Modifiers);
        Assert.Equal("Ctrl+Shift", gesture.DisplayName);
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
    public async Task ConcurrentSlotRegistrationPersistsBothLatestGestures()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsDesktopLocalSettingsService settings = new(registry);
        await using WindowsGlobalHotKeyService service = new(
            new WindowsHotKeyRegistrar(new FakeWindowsHotKeyNative()),
            settings);
        GlobalHotKeyGestureCreationResult primaryCreation = service.CreateGesture(
            GlobalHotKeyModifiers.None,
            "LeftCtrl");
        GlobalHotKeyGesture primary =
            Assert.IsType<GlobalHotKeyGesture>(primaryCreation.Gesture);
        GlobalHotKeyGestureCreationResult creation = service.CreateGesture(
            GlobalHotKeySlot.Double,
            GlobalHotKeyModifiers.None,
            "K");
        GlobalHotKeyGesture doubleGesture =
            Assert.IsType<GlobalHotKeyGesture>(creation.Gesture);

        Task<GlobalHotKeyRegistrationResult> primaryTask = service.RegisterAsync(
            GlobalHotKeySlot.Primary,
            primary,
            CancellationToken.None).AsTask();
        Task<GlobalHotKeyRegistrationResult> doubleTask = service.RegisterAsync(
            GlobalHotKeySlot.Double,
            doubleGesture,
            CancellationToken.None).AsTask();
        GlobalHotKeyRegistrationResult[] results = await Task.WhenAll(
            primaryTask,
            doubleTask);

        Assert.All(results, result => Assert.Equal(
            GlobalHotKeyRegistrationStatus.Registered,
            result.Status));
        Assert.Equal(GlobalHotKeyModifiers.NoRepeat, doubleGesture.Modifiers);
        Assert.Equal(primary, settings.Current.PrimaryHotKey);
        Assert.Equal(doubleGesture, settings.Current.DoubleHotKey);
        Assert.Equal(primary, service.GetCurrentGesture(GlobalHotKeySlot.Primary));
        Assert.Equal(doubleGesture, service.GetCurrentGesture(GlobalHotKeySlot.Double));
    }

    [WindowsFact]
    public async Task ActiveRegistrationIdentifiersRaiseDistinctTriggerSources()
    {
        WindowsHotKeyRegistrar registrar = new(new FakeWindowsHotKeyNative());
        WindowsDesktopLocalSettingsService settings = new(new FakeWindowsRegistryStore());
        await using WindowsGlobalHotKeyService service = new(registrar, settings);
        GlobalHotKeyGesture doubleGesture = CreateGesture(0x4B, "Ctrl+Alt+K");
        await service.RegisterAsync(
            GlobalHotKeySlot.Primary,
            GlobalHotKeyGesture.WindowsDefault,
            CancellationToken.None);
        await service.RegisterAsync(
            GlobalHotKeySlot.Double,
            doubleGesture,
            CancellationToken.None);
        List<GlobalHotKeySlot> sources = [];
        TaskCompletionSource receivedBoth = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.Triggered += (_, e) =>
        {
            lock (sources)
            {
                sources.Add(e.Source);
                if (sources.Count == 2)
                {
                    receivedBoth.TrySetResult();
                }
            }
        };
        int primaryIdentifier = registrar.GetCurrentIdentifier(GlobalHotKeySlot.Primary)!.Value;
        int doubleIdentifier = registrar.GetCurrentIdentifier(GlobalHotKeySlot.Double)!.Value;

        Assert.True(service.QueueActiveHotKeyIdentifier(primaryIdentifier));
        Assert.True(service.QueueActiveHotKeyIdentifier(doubleIdentifier));
        await receivedBoth.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([GlobalHotKeySlot.Primary, GlobalHotKeySlot.Double], sources);
    }

    [WindowsFact]
    public async Task ModifierMainKeysAndModifierOnlyChordDeliverNativeMessages()
    {
        NativeModifierHotKeyCase[] cases =
        [
            new(
                "LeftCtrl",
                GlobalHotKeyModifiers.None,
                [0x11],
                GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.NoRepeat,
                "Ctrl"),
            new(
                "LeftAlt",
                GlobalHotKeyModifiers.None,
                [0x12],
                GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
                "Alt"),
            new(
                "LeftShift",
                GlobalHotKeyModifiers.None,
                [0x10],
                GlobalHotKeyModifiers.Shift | GlobalHotKeyModifiers.NoRepeat,
                "Shift"),
            new(
                "LWin",
                GlobalHotKeyModifiers.None,
                [0x5B],
                GlobalHotKeyModifiers.Windows | GlobalHotKeyModifiers.NoRepeat,
                "Win"),
            new(
                "RWin",
                GlobalHotKeyModifiers.None,
                [0x5C],
                GlobalHotKeyModifiers.Windows | GlobalHotKeyModifiers.NoRepeat,
                "Right Win"),
            new(
                "LeftShift",
                GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Shift,
                [0x11, 0x10],
                GlobalHotKeyModifiers.Control |
                GlobalHotKeyModifiers.Shift |
                GlobalHotKeyModifiers.NoRepeat,
                "Ctrl+Shift"),
        ];

        foreach (NativeModifierHotKeyCase testCase in cases)
        {
            await using WindowsGlobalHotKeyService service = CreateHotKeyService();
            GlobalHotKeyGestureCreationResult creation = service.CreateGesture(
                GlobalHotKeySlot.Double,
                testCase.Modifiers,
                testCase.KeyName);
            GlobalHotKeyGesture gesture =
                Assert.IsType<GlobalHotKeyGesture>(creation.Gesture);
            Assert.Equal(testCase.ExpectedModifiers, gesture.Modifiers);
            Assert.Equal(testCase.ExpectedDisplayName, gesture.DisplayName);
            TaskCompletionSource triggered = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.Triggered += (_, e) =>
            {
                if (e.Source == GlobalHotKeySlot.Double)
                {
                    triggered.TrySetResult();
                }
            };
            GlobalHotKeyRegistrationResult registration = await service.RegisterAsync(
                GlobalHotKeySlot.Double,
                gesture,
                CancellationToken.None);

            Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, registration.Status);
            try
            {
                SendChord(testCase.VirtualKeys);
                await triggered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                ReleaseKeys(testCase.VirtualKeys);
                await service.ClearAsync(
                    GlobalHotKeySlot.Double,
                    CancellationToken.None);
            }
        }
    }

    [WindowsFact]
    public async Task HeldModifierMainKeyDoesNotProduceASecondNativeTrigger()
    {
        await using WindowsGlobalHotKeyService service = CreateHotKeyService();
        GlobalHotKeyGestureCreationResult creation = service.CreateGesture(
            GlobalHotKeySlot.Double,
            GlobalHotKeyModifiers.None,
            "LeftAlt");
        GlobalHotKeyGesture gesture =
            Assert.IsType<GlobalHotKeyGesture>(creation.Gesture);
        TaskCompletionSource firstTrigger = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondTrigger = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int triggerCount = 0;
        service.Triggered += (_, e) =>
        {
            if (e.Source != GlobalHotKeySlot.Double)
            {
                return;
            }

            int count = Interlocked.Increment(ref triggerCount);
            if (count == 1)
            {
                firstTrigger.TrySetResult();
            }
            else if (count == 2)
            {
                secondTrigger.TrySetResult();
            }
        };
        GlobalHotKeyRegistrationResult registration = await service.RegisterAsync(
            GlobalHotKeySlot.Double,
            gesture,
            CancellationToken.None);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, registration.Status);
        try
        {
            SendKey(0x12, keyUp: false);
            await firstTrigger.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SendKey(0x12, keyUp: false);
            await Task.Delay(150);
            Assert.Equal(1, Volatile.Read(ref triggerCount));

            SendKey(0x12, keyUp: true);
            SendKey(0x12, keyUp: false);
            SendKey(0x12, keyUp: true);
            await secondTrigger.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref triggerCount));
        }
        finally
        {
            SendKey(0x12, keyUp: true);
            await service.ClearAsync(
                GlobalHotKeySlot.Double,
                CancellationToken.None);
        }
    }

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
        new WindowsDesktopLocalSettingsService(new FakeWindowsRegistryStore()));

    private static void SendChord(IReadOnlyList<byte> virtualKeys)
    {
        foreach (byte virtualKey in virtualKeys)
        {
            SendKey(virtualKey, keyUp: false);
        }

        ReleaseKeys(virtualKeys);
    }

    private static void ReleaseKeys(IReadOnlyList<byte> virtualKeys)
    {
        for (int index = virtualKeys.Count - 1; index >= 0; index--)
        {
            SendKey(virtualKeys[index], keyUp: true);
        }
    }

    private static void SendKey(byte virtualKey, bool keyUp) =>
        WindowsHotKeyTestInput.KeybdEvent(
            virtualKey,
            scanCode: 0,
            keyUp ? WindowsNativeConstants.KeyEventKeyUp : 0,
            extraInfo: 0);

    private static GlobalHotKeyGesture CreateGesture(uint virtualKey, string displayName) => new(
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.NoRepeat,
        virtualKey,
        displayName);

    private sealed record NativeModifierHotKeyCase(
        string KeyName,
        GlobalHotKeyModifiers Modifiers,
        byte[] VirtualKeys,
        GlobalHotKeyModifiers ExpectedModifiers,
        string ExpectedDisplayName);
}

internal static partial class WindowsHotKeyTestInput
{
    [LibraryImport("user32.dll", EntryPoint = "keybd_event")]
    internal static partial void KeybdEvent(
        byte virtualKey,
        byte scanCode,
        uint flags,
        nuint extraInfo);
}
