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
        GlobalHotKeyGesture doubleGesture = CreateGesture(0x4B, "Ctrl+Alt+K");

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
                Assert.True((((GlobalHotKeyModifiers)doubleRegistration.Modifiers) &
                    GlobalHotKeyModifiers.NoRepeat) != 0);
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
        GlobalHotKeyGesture primary = CreateGesture(0x4A, "Ctrl+Alt+J");
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
            "1");

        WindowsDesktopLocalSettingsService current = new(registry);

        Assert.Equal("2", WindowsDesktopLocalSettingsService.CurrentVersion);
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
    public void RejectsGestureWithoutModifier()
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.None,
            "K");

        Assert.Equal(GlobalHotKeyGestureCreationStatus.MissingModifier, result.Status);
        Assert.Null(result.Gesture);
    }

    [Fact]
    public void CreatesModifierlessDoubleGestureWithNoRepeat()
    {
        GlobalHotKeyGestureCreationResult result = WindowsHotKeyKeyMap.CreateGesture(
            GlobalHotKeyModifiers.None,
            "K",
            requireModifier: false);

        Assert.Equal(GlobalHotKeyGestureCreationStatus.Created, result.Status);
        GlobalHotKeyGesture gesture = Assert.IsType<GlobalHotKeyGesture>(result.Gesture);
        Assert.Equal(0x4Bu, gesture.VirtualKey);
        Assert.Equal(GlobalHotKeyModifiers.NoRepeat, gesture.Modifiers);
        Assert.Equal("K", gesture.DisplayName);
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
    public async Task ConcurrentSlotRegistrationPersistsBothLatestGestures()
    {
        FakeWindowsRegistryStore registry = new();
        WindowsDesktopLocalSettingsService settings = new(registry);
        GlobalHotKeyGesture primary = CreateGesture(0x4A, "Ctrl+Alt+J");
        await using WindowsGlobalHotKeyService service = new(
            new WindowsHotKeyRegistrar(new FakeWindowsHotKeyNative()),
            settings);
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

    private static GlobalHotKeyGesture CreateGesture(uint virtualKey, string displayName) => new(
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.NoRepeat,
        virtualKey,
        displayName);
}
