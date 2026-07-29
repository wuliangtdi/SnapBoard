using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Application.Sync;
using SnapBoard.Application.Updates;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Desktop.Views;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class SecondaryWindowHeadlessTests
{
    [AvaloniaFact]
    public void SettingsWindowCanBeClosedAndRecreated()
    {
        FakeGlobalHotKeyService hotKeyService = new();
        FakeAutoStartService autoStartService = new();

        SettingsWindow first = CreateSettingsWindow(hotKeyService, autoStartService);
        first.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(first.FindControl<Button>("HotKeyCaptureButton"));
        first.Close();
        Dispatcher.UIThread.RunJobs();

        SettingsWindow second = CreateSettingsWindow(hotKeyService, autoStartService);
        try
        {
            second.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(first, second);
            Assert.True(second.IsVisible);
        }
        finally
        {
            second.Close();
        }
    }

    [AvaloniaFact]
    public async Task SettingsWindowRendersTheBrandedLayout()
    {
        FakeSyncService syncService = new(configured: true);
        SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            storageManagementService: new FakeStorageManagementService(),
            storagePlatformService: new FakeStoragePlatformService(),
            requestStorageMigration: static (_, _) => ValueTask.CompletedTask,
            syncService: syncService,
            historySettingsService: new FakeHistorySettingsService(),
            applicationUpdateService: new FakeApplicationUpdateService());
        await viewModel.InitializeStorageAsync();
        viewModel.SyncActiveSpaceId = "11111111-1111-1111-1111-111111111111";
        viewModel.SyncRecoveryMaterialPath =
            @"C:\SnapBoardData\recovery\sync-space-11111111111111111111111111111111-v1.recovery";
        SettingsWindow window = new() { DataContext = viewModel };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            viewModel.SyncActiveSpaceId = "11111111-1111-1111-1111-111111111111";
            viewModel.SyncRecoveryMaterialPath =
                @"C:\SnapBoardData\recovery\sync-space-11111111111111111111111111111111-v1.recovery";

            Assert.Equal(new Avalonia.Size(960, 720), window.ClientSize);
            Assert.Equal("闪剪", window.Title);
            Assert.NotNull(window.Icon);
            Assert.NotNull(window.FindControl<Button>("HotKeyCaptureButton"));
            ListBox navigation = Assert.IsType<ListBox>(
                window.FindControl<ListBox>("SettingsNavigationList"));
            ListBoxItem permissionsNavigationItem = Assert.IsType<ListBoxItem>(
                window.FindControl<ListBoxItem>("PermissionsNavigationItem"));
            ScrollViewer contentScroller = Assert.IsType<ScrollViewer>(
                window.FindControl<ScrollViewer>("SettingsContentScrollViewer"));
            StackPanel generalSection = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("GeneralSettingsSection"));
            StackPanel historySection = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("HistorySettingsSection"));
            StackPanel syncSection = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("SyncSettingsSection"));
            StackPanel storageSection = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("StorageSettingsSection"));
            StackPanel updateSection = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("ApplicationUpdateSettingsSection"));
            StackPanel permissionSection = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("PermissionSettingsSection"));
            TextBox syncEndpoint = Assert.IsType<TextBox>(
                window.FindControl<TextBox>("SyncEndpointTextBox"));
            Button configureSync = Assert.IsType<Button>(
                window.FindControl<Button>("ConfigureSyncButton"));
            Button saveHistory = Assert.IsType<Button>(
                window.FindControl<Button>("SaveHistorySettingsButton"));
            ListBox syncPaneNavigation = Assert.IsType<ListBox>(
                window.FindControl<ListBox>("SyncSettingsPaneList"));
            StackPanel syncOverviewPane = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("SyncOverviewPane"));
            StackPanel syncConfigurationPane = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("SyncConfigurationPane"));
            StackPanel syncMigrationPane = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("SyncMigrationPane"));
            Assert.IsType<ComboBox>(window.FindControl<ComboBox>("SyncFrequencyComboBox"));
            Assert.IsType<Button>(window.FindControl<Button>("SaveSyncFrequencyButton"));
            Assert.IsType<ComboBox>(window.FindControl<ComboBox>("UpdateChannelComboBox"));
            Assert.IsType<ComboBox>(window.FindControl<ComboBox>("UpdateSourceComboBox"));
            Assert.IsType<Button>(
                window.FindControl<Button>("CheckForApplicationUpdatesButton"));
            Assert.IsType<Button>(
                window.FindControl<Button>("DownloadApplicationUpdateButton"));
            TextBox providerEndpoint = Assert.IsType<TextBox>(
                window.FindControl<TextBox>("ProviderMigrationTargetEndpointTextBox"));
            StackPanel providerSection = Assert.IsType<StackPanel>(
                window.FindControl<StackPanel>("ProviderMigrationSection"));
            Button startProviderMigration = Assert.IsType<Button>(
                window.FindControl<Button>("StartOrProvideProviderMigrationButton"));
            Assert.IsType<ItemsControl>(
                window.FindControl<ItemsControl>("ProviderMigrationDevicesItems"));
            Assert.Equal(@"C:\SnapBoardData", viewModel.CurrentStorageDirectory);
            Assert.Contains("1 MiB", viewModel.StorageUsageText, StringComparison.Ordinal);
            Assert.Equal((int)SettingsSection.General, navigation.SelectedIndex);
            Assert.True(generalSection.IsVisible);
            Assert.False(historySection.IsVisible);
            Assert.False(syncSection.IsVisible);
            Assert.False(storageSection.IsVisible);
            Assert.False(updateSection.IsVisible);
            Assert.False(permissionSection.IsVisible);
            Assert.False(viewModel.IsPermissionSectionVisible);
            Assert.True(permissionsNavigationItem.IsVisible);
            Assert.False(permissionsNavigationItem.IsEnabled);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(960, frame.PixelSize.Width);
            Assert.Equal(720, frame.PixelSize.Height);

            string? capturePath =
                Environment.GetEnvironmentVariable("SNAPBOARD_SETTINGS_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                frame.Save(capturePath, PngBitmapEncoderOptions.Default);
            }

            navigation.SelectedIndex = (int)SettingsSection.HistoryAndPrivacy;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                (int)SettingsSection.HistoryAndPrivacy,
                viewModel.SelectedSettingsSectionIndex);
            Assert.False(generalSection.IsVisible);
            Assert.True(historySection.IsVisible);
            Assert.True(saveHistory.IsVisible);
            using var historyFrame = window.CaptureRenderedFrame();
            Assert.NotNull(historyFrame);
            string? historyCapturePath =
                Environment.GetEnvironmentVariable("SNAPBOARD_SETTINGS_HISTORY_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(historyCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyCapturePath)!);
                historyFrame.Save(historyCapturePath, PngBitmapEncoderOptions.Default);
            }

            navigation.SelectedIndex = (int)SettingsSection.Sync;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((int)SettingsSection.Sync, viewModel.SelectedSettingsSectionIndex);
            Assert.True(syncSection.IsVisible);
            Assert.Equal((int)SyncSettingsPane.Overview, syncPaneNavigation.SelectedIndex);
            Assert.True(syncOverviewPane.IsVisible);
            Assert.False(syncConfigurationPane.IsVisible);
            Assert.False(syncMigrationPane.IsVisible);
            using var syncFrame = window.CaptureRenderedFrame();
            Assert.NotNull(syncFrame);
            Assert.Equal(960, syncFrame.PixelSize.Width);
            Assert.Equal(720, syncFrame.PixelSize.Height);
            string? syncCapturePath =
                Environment.GetEnvironmentVariable("SNAPBOARD_SETTINGS_SYNC_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(syncCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(syncCapturePath)!);
                syncFrame.Save(syncCapturePath, PngBitmapEncoderOptions.Default);
            }

            syncPaneNavigation.SelectedIndex = (int)SyncSettingsPane.Configuration;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                (int)SyncSettingsPane.Configuration,
                viewModel.SelectedSyncSettingsPaneIndex);
            Assert.False(syncOverviewPane.IsVisible);
            Assert.True(syncConfigurationPane.IsVisible);
            Assert.False(syncMigrationPane.IsVisible);
            Assert.True(syncEndpoint.IsVisible);
            Assert.True(configureSync.IsVisible);
            using var syncConfigurationFrame = window.CaptureRenderedFrame();
            Assert.NotNull(syncConfigurationFrame);
            string? syncConfigurationCapturePath = Environment.GetEnvironmentVariable(
                "SNAPBOARD_SETTINGS_SYNC_CONFIGURATION_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(syncConfigurationCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(syncConfigurationCapturePath)!);
                syncConfigurationFrame.Save(
                    syncConfigurationCapturePath,
                    PngBitmapEncoderOptions.Default);
            }

            syncPaneNavigation.SelectedIndex = (int)SyncSettingsPane.Migration;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                (int)SyncSettingsPane.Migration,
                viewModel.SelectedSyncSettingsPaneIndex);
            Assert.False(syncOverviewPane.IsVisible);
            Assert.False(syncConfigurationPane.IsVisible);
            Assert.True(syncMigrationPane.IsVisible);
            Assert.True(providerSection.IsVisible);
            Assert.True(providerEndpoint.IsVisible);
            Assert.True(startProviderMigration.IsVisible);
            using var providerMigrationInputFrame = window.CaptureRenderedFrame();
            Assert.NotNull(providerMigrationInputFrame);
            string? providerMigrationInputCapturePath = Environment.GetEnvironmentVariable(
                "SNAPBOARD_SETTINGS_PROVIDER_MIGRATION_INPUT_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(providerMigrationInputCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(
                    providerMigrationInputCapturePath)!);
                providerMigrationInputFrame.Save(
                    providerMigrationInputCapturePath,
                    PngBitmapEncoderOptions.Default);
            }

            viewModel.ProviderMigrationTargetEndpoint = "https://new.example.test/dav/";
            viewModel.ProviderMigrationTargetRoot = "SnapBoard/v2";
            viewModel.ProviderMigrationTargetUsername = "target-user";
            viewModel.ProviderMigrationTargetPassword = "target-secret";
            await viewModel.StartOrProvideProviderMigrationCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, viewModel.ProviderMigrationDevices.Count);
            using var providerMigrationFrame = window.CaptureRenderedFrame();
            Assert.NotNull(providerMigrationFrame);
            string? providerMigrationCapturePath = Environment.GetEnvironmentVariable(
                "SNAPBOARD_SETTINGS_PROVIDER_MIGRATION_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(providerMigrationCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(providerMigrationCapturePath)!);
                providerMigrationFrame.Save(
                    providerMigrationCapturePath,
                    PngBitmapEncoderOptions.Default);
            }

            syncPaneNavigation.SelectedIndex = (int)SyncSettingsPane.Configuration;
            Dispatcher.UIThread.RunJobs();
            contentScroller.Offset = new Avalonia.Vector(0, 200);
            Dispatcher.UIThread.RunJobs();
            Assert.True(contentScroller.Offset.Y > 0);
            navigation.SelectedIndex = (int)SettingsSection.Storage;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, contentScroller.Offset.Y);
            Assert.True(storageSection.IsVisible);
            Assert.False(syncSection.IsVisible);
            using var storageFrame = window.CaptureRenderedFrame();
            Assert.NotNull(storageFrame);
            string? storageCapturePath = Environment.GetEnvironmentVariable(
                "SNAPBOARD_SETTINGS_STORAGE_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(storageCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storageCapturePath)!);
                storageFrame.Save(storageCapturePath, PngBitmapEncoderOptions.Default);
            }

            navigation.SelectedIndex = (int)SettingsSection.Updates;
            Dispatcher.UIThread.RunJobs();
            Assert.True(updateSection.IsVisible);
            Assert.False(storageSection.IsVisible);
            using var updateFrame = window.CaptureRenderedFrame();
            Assert.NotNull(updateFrame);
            string? updateCapturePath = Environment.GetEnvironmentVariable(
                "SNAPBOARD_SETTINGS_UPDATE_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(updateCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(updateCapturePath)!);
                updateFrame.Save(updateCapturePath, PngBitmapEncoderOptions.Default);
            }

            navigation.SelectedIndex = (int)SettingsSection.General;
            window.Width = 820;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(820, window.ClientSize.Width);
            using var narrowFrame = window.CaptureRenderedFrame();
            Assert.NotNull(narrowFrame);
            Assert.Equal(820, narrowFrame.PixelSize.Width);
            Assert.Equal(720, narrowFrame.PixelSize.Height);
            string? narrowCapturePath = Environment.GetEnvironmentVariable(
                "SNAPBOARD_SETTINGS_NARROW_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(narrowCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(narrowCapturePath)!);
                narrowFrame.Save(narrowCapturePath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void SettingsNavigationPreservesPendingValuesAndCancelsActiveShortcutCapture()
    {
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            syncService: new FakeSyncService(configured: true),
            historySettingsService: new FakeHistorySettingsService());
        viewModel.BeginHotKeyCapture();
        Assert.True(viewModel.CaptureHotKey(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt,
            "K"));
        viewModel.SyncEndpoint = "https://pending.example.test/dav/";
        viewModel.SelectedSyncSettingsPaneIndex = (int)SyncSettingsPane.Configuration;
        viewModel.BeginDoubleHotKeyCapture();

        viewModel.SelectedSettingsSectionIndex = (int)SettingsSection.HistoryAndPrivacy;

        Assert.False(viewModel.IsCapturingHotKey);
        Assert.True(viewModel.HasPendingHotKeyChange);
        Assert.Equal("Ctrl+Alt+K", viewModel.HotKeyDisplayName);
        Assert.Equal("https://pending.example.test/dav/", viewModel.SyncEndpoint);

        viewModel.SelectedSettingsSectionIndex = (int)SettingsSection.Sync;

        Assert.Equal(
            (int)SyncSettingsPane.Configuration,
            viewModel.SelectedSyncSettingsPaneIndex);
        Assert.Equal("https://pending.example.test/dav/", viewModel.SyncEndpoint);
    }

    [Fact]
    public async Task SettingsViewModelCapturesAndAppliesCustomHotKey()
    {
        FakeGlobalHotKeyService hotKeyService = new();
        SettingsViewModel viewModel = new(hotKeyService, new FakeAutoStartService());

        viewModel.BeginHotKeyCapture();
        bool captured = viewModel.CaptureHotKey(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt,
            "K");

        Assert.True(captured);
        Assert.Equal("Ctrl+Alt+K", viewModel.HotKeyDisplayName);
        Assert.True(viewModel.HasPendingHotKeyChange);

        await viewModel.ApplyHotKeyCommand.ExecuteAsync(null);

        Assert.Equal("Ctrl+Alt+K", hotKeyService.ConfiguredGesture.DisplayName);
        Assert.Equal(0x4Bu, hotKeyService.ConfiguredGesture.VirtualKey);
        Assert.False(viewModel.HasPendingHotKeyChange);
    }

    [Fact]
    public async Task DoubleHotKeyCaptureAllowsAndAppliesSingleKey()
    {
        FakeGlobalHotKeyService hotKeyService = new();
        using SettingsViewModel viewModel = new(
            hotKeyService,
            new FakeAutoStartService());

        viewModel.BeginDoubleHotKeyCapture();

        Assert.Equal("请按下一个按键或组合键，Esc 取消", viewModel.DoubleHotKeyStatus);
        Assert.True(viewModel.CaptureHotKey(GlobalHotKeyModifiers.None, "K"));
        Assert.Equal("K", viewModel.DoubleHotKeyDisplayName);
        Assert.True(viewModel.HasPendingDoubleHotKeyChange);

        await viewModel.ApplyDoubleHotKeyCommand.ExecuteAsync(null);

        GlobalHotKeyGesture configured = Assert.IsType<GlobalHotKeyGesture>(
            hotKeyService.ConfiguredDoubleGesture);
        Assert.Equal(GlobalHotKeyModifiers.NoRepeat, configured.Modifiers);
        Assert.Equal(0x4Bu, configured.VirtualKey);
        Assert.False(viewModel.HasPendingDoubleHotKeyChange);
    }

    [AvaloniaFact]
    public void SettingsWindowShowsTwoSlotHotKeysAndDefaultProtectionToggles()
    {
        FakeDesktopLocalSettingsService localSettings = new();
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            localSettings: localSettings,
            foregroundWindowStateService: new FakeForegroundWindowStateService());
        SettingsWindow window = new() { DataContext = viewModel };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.IsType<Button>(window.FindControl<Button>("DoubleHotKeyCaptureButton"));
            Assert.Equal(
                "连按两次快捷键打开快速窗口",
                window.FindControl<TextBlock>("DoubleHotKeyHeading")?.Text);
            ToggleSwitch hotKeyProtection = Assert.IsType<ToggleSwitch>(
                window.FindControl<ToggleSwitch>("DisableHotKeysWhenProtectedToggle"));
            ToggleSwitch captureProtection = Assert.IsType<ToggleSwitch>(
                window.FindControl<ToggleSwitch>("PauseCaptureWhenProtectedToggle"));
            ListBox protectionScope = Assert.IsType<ListBox>(
                window.FindControl<ListBox>("ForegroundProtectionScopeSelector"));
            Assert.True(hotKeyProtection.IsChecked is true);
            Assert.True(captureProtection.IsChecked is true);
            Assert.Equal((int)ForegroundProtectionScope.FullScreenOnly, protectionScope.SelectedIndex);
            Assert.Equal(
                "保护范围",
                window.FindControl<TextBlock>("ProtectionScopeHeading")?.Text);
            Assert.Equal(
                "保护期间停用全局快捷键",
                window.FindControl<TextBlock>("DisableHotKeysWhenProtectedHeading")?.Text);
            Assert.Equal(
                "保护期间停止记录复制内容",
                window.FindControl<TextBlock>("PauseCaptureWhenProtectedHeading")?.Text);
            Assert.Equal("未设置", viewModel.DoubleHotKeyDisplayName);
            Assert.False(viewModel.HasConfiguredDoubleHotKey);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void HotKeyCaptureTargetsAreMutuallyExclusiveAndRejectDuplicates()
    {
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService());

        viewModel.BeginHotKeyCapture();
        Assert.True(viewModel.IsCapturingPrimaryHotKey);
        viewModel.BeginDoubleHotKeyCapture();
        Assert.False(viewModel.IsCapturingPrimaryHotKey);
        Assert.True(viewModel.IsCapturingDoubleHotKey);

        Assert.True(viewModel.CaptureHotKey(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Shift,
            "V"));

        Assert.Equal("两组快捷键不能相同", viewModel.DoubleHotKeyStatus);
        viewModel.BeginHotKeyCapture();
        Assert.True(viewModel.IsCapturingPrimaryHotKey);
        Assert.False(viewModel.IsCapturingDoubleHotKey);
    }

    [Fact]
    public async Task DoubleHotKeyConflictKeepsBothPreviousSlots()
    {
        FakeGlobalHotKeyService hotKeyService = new();
        GlobalHotKeyGesture originalDouble = Gesture("Ctrl+Alt+K", 0x4B);
        await hotKeyService.RegisterAsync(
            GlobalHotKeySlot.Double,
            originalDouble,
            CancellationToken.None);
        using SettingsViewModel viewModel = new(hotKeyService, new FakeAutoStartService());
        viewModel.BeginDoubleHotKeyCapture();
        Assert.True(viewModel.CaptureHotKey(
            GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.Shift,
            "M"));
        hotKeyService.NextDoubleRegistrationStatus = GlobalHotKeyRegistrationStatus.Conflict;

        await viewModel.ApplyDoubleHotKeyCommand.ExecuteAsync(null);

        Assert.Equal(GlobalHotKeyGesture.Default, hotKeyService.ConfiguredGesture);
        Assert.Equal(originalDouble, hotKeyService.ConfiguredDoubleGesture);
        Assert.Equal(originalDouble.DisplayName, viewModel.DoubleHotKeyDisplayName);
        Assert.Equal("快捷键已被其他应用占用，原配置已保留", viewModel.DoubleHotKeyStatus);
    }

    [Fact]
    public async Task ClearingDoubleHotKeyDoesNotChangePrimarySlot()
    {
        FakeGlobalHotKeyService hotKeyService = new();
        await hotKeyService.RegisterAsync(
            GlobalHotKeySlot.Double,
            Gesture("Ctrl+Alt+K", 0x4B),
            CancellationToken.None);
        using SettingsViewModel viewModel = new(hotKeyService, new FakeAutoStartService());

        await viewModel.ClearDoubleHotKeyCommand.ExecuteAsync(null);

        Assert.Equal(GlobalHotKeyGesture.Default, hotKeyService.ConfiguredGesture);
        Assert.Null(hotKeyService.ConfiguredDoubleGesture);
        Assert.Equal("未设置", viewModel.DoubleHotKeyDisplayName);
        Assert.False(viewModel.HasConfiguredDoubleHotKey);
    }

    [Fact]
    public void ProtectionSettingsPersistImmediatelyAndUnknownStateUsesConservativeText()
    {
        FakeDesktopLocalSettingsService localSettings = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = new ForegroundWindowStateResult(
                ForegroundWindowState.Unknown,
                IsSnapBoard: false,
                Identity: null,
                ForegroundWindowDiagnosticCode.NativeFailure),
        };
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            localSettings: localSettings,
            foregroundWindowStateService: foreground);

        viewModel.SelectedForegroundProtectionScopeIndex =
            (int)ForegroundProtectionScope.FullScreenAndMaximized;
        viewModel.IsDisableGlobalHotKeysWhenProtected = false;
        viewModel.IsPauseClipboardCaptureWhenProtected = false;

        Assert.Equal(3, localSettings.UpdateCount);
        Assert.Equal(
            ForegroundProtectionScope.FullScreenAndMaximized,
            localSettings.Current.ProtectionScope);
        Assert.False(localSettings.Current.DisableGlobalHotKeysWhenProtected);
        Assert.False(localSettings.Current.PauseClipboardCaptureWhenProtected);
        Assert.Equal("当前平台暂时无法判断前台窗口状态", viewModel.ForegroundWindowStatus);
    }

    [Fact]
    public void MaximizedStatusReflectsSelectedProtectionScope()
    {
        FakeDesktopLocalSettingsService localSettings = new();
        FakeForegroundWindowStateService foreground = new()
        {
            Result = new ForegroundWindowStateResult(
                ForegroundWindowState.Maximized,
                IsSnapBoard: false,
                new ForegroundWindowIdentity(1, 2),
                ForegroundWindowDiagnosticCode.None),
        };
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            localSettings: localSettings,
            foregroundWindowStateService: foreground);

        Assert.Equal(
            "当前检测到窗口最大化，按当前范围不启用保护",
            viewModel.ForegroundWindowStatus);

        viewModel.SelectedForegroundProtectionScopeIndex =
            (int)ForegroundProtectionScope.FullScreenAndMaximized;

        Assert.Equal(
            "当前检测到窗口最大化，已进入保护范围",
            viewModel.ForegroundWindowStatus);
    }

    private static GlobalHotKeyGesture Gesture(string displayName, uint virtualKey) => new(
        GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
        virtualKey,
        displayName);

    [AvaloniaFact]
    public async Task InvalidStorageTargetDisplaysTheReasonAndKeepsMigrationDisabled()
    {
        const string target = @"D:\ProgramData\SnapBoard_Data";
        FakeStorageManagementService storage = new()
        {
            ValidationResult = new StorageLocationValidationResult(
                false,
                target,
                "volume-2",
                AvailableBytes: 0,
                RequiredBytes: 0,
                StorageLocationValidationError.InsecurePermissions,
                "insecure-acl"),
        };
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            storageManagementService: storage,
            storagePlatformService: new FakeStoragePlatformService(),
            requestStorageMigration: static (_, _) => ValueTask.CompletedTask);

        await viewModel.InitializeStorageAsync();
        await viewModel.SelectStorageTargetAsync(target);

        Assert.True(viewModel.IsStorageMigrationConfirmationVisible);
        Assert.False(viewModel.CanConfirmStorageMigration);
        Assert.Equal("无法使用所选目录", viewModel.StorageTargetTitle);
        Assert.Equal(target, viewModel.SelectedStorageDirectory);
        Assert.Contains("权限", viewModel.StorageTargetDetails, StringComparison.Ordinal);
        Assert.StartsWith("未更改位置", viewModel.StorageStatus, StringComparison.Ordinal);
        viewModel.SelectedSettingsSectionIndex = (int)SettingsSection.Storage;

        SettingsWindow window = new() { DataContext = viewModel };
        try
        {
            window.Show();
            TextBlock status = Assert.IsType<TextBlock>(
                window.FindControl<TextBlock>("StorageStatusText"));
            status.BringIntoView();
            Dispatcher.UIThread.RunJobs();

            Assert.True(status.IsVisible);
            Assert.Contains("未更改位置", status.Text, StringComparison.Ordinal);
            Assert.Contains("权限", status.Text, StringComparison.Ordinal);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(960, frame.PixelSize.Width);
            Assert.Equal(720, frame.PixelSize.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void StorageMigrationConfirmationWindowRendersTheRestartWarning()
    {
        const string target = @"D:\ProgramData\SnapBoard_Data";
        StorageMigrationConfirmationWindow window = new(
            target,
            "需迁移约 3 MiB；目标可用 200 GiB。");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Avalonia.Size(500, 340), window.ClientSize);
            Assert.Equal(target, window.FindControl<TextBlock>("TargetDirectoryText")?.Text);
            Assert.NotNull(window.FindControl<Button>("CancelButton"));
            Assert.NotNull(window.FindControl<Button>("ConfirmButton"));
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(500, frame.PixelSize.Width);
            Assert.Equal(340, frame.PixelSize.Height);

            string? capturePath = Environment.GetEnvironmentVariable(
                "SNAPBOARD_STORAGE_MIGRATION_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                frame.Save(capturePath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task StorageMigrationConfirmationWindowIsOwnedAndCancelReturnsFalse()
    {
        Window owner = new() { Width = 700, Height = 720 };
        StorageMigrationConfirmationWindow dialog = new(
            @"D:\ProgramData\SnapBoard_Data",
            "需迁移约 3 MiB；目标可用 200 GiB。");
        try
        {
            owner.Show();
            Task<bool> result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(dialog, owner.OwnedWindows);
            Button cancel = Assert.IsType<Button>(dialog.FindControl<Button>("CancelButton"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(await result);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }

            owner.Close();
        }
    }

    [Fact]
    public async Task ConfirmedStorageMigrationUsesTheValidatedTarget()
    {
        const string target = @"D:\ProgramData\SnapBoard_Data";
        string? requestedTarget = null;
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            storageManagementService: new FakeStorageManagementService(),
            storagePlatformService: new FakeStoragePlatformService(),
            requestStorageMigration: (directory, _) =>
            {
                requestedTarget = directory;
                return ValueTask.CompletedTask;
            });

        await viewModel.SelectStorageTargetAsync(target);
        await viewModel.ConfirmStorageMigrationCommand.ExecuteAsync(null);

        Assert.Equal(target, requestedTarget);
        Assert.Contains("自动重新启动", viewModel.StorageStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalStorageValidationFailureIsExposedForTheErrorDialog()
    {
        const string target = @"D:\ProgramData\SnapBoard_Data";
        StorageLocationValidationResult finalValidation = new(
            false,
            target,
            "volume-2",
            AvailableBytes: 0,
            RequiredBytes: 0,
            StorageLocationValidationError.ExistingStorage,
            "target-not-empty");
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            storageManagementService: new FakeStorageManagementService(),
            storagePlatformService: new FakeStoragePlatformService(),
            requestStorageMigration: (_, _) => ValueTask.FromException(
                new StorageLocationValidationException(finalValidation)));

        await viewModel.SelectStorageTargetAsync(target);
        await viewModel.ConfirmStorageMigrationCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsStorageBusy);
        Assert.False(viewModel.CanConfirmStorageMigration);
        Assert.Contains("确认期间出现了文件", viewModel.StorageMigrationErrorMessage,
            StringComparison.Ordinal);
        Assert.Contains("迁移未启动", viewModel.StorageStatus, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void StorageMigrationErrorWindowRendersTheProtectedState()
    {
        const string target = @"D:\ProgramData\SnapBoard_Data";
        StorageMigrationErrorWindow window = new(
            target,
            "目标目录在确认期间出现了文件或子目录。为保护已有内容，迁移没有开始。" +
            "请清空该目录，或重新选择一个空目录。");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new Avalonia.Size(500, 350), window.ClientSize);
            Assert.Equal(target, window.FindControl<TextBlock>("TargetDirectoryText")?.Text);
            Assert.Contains(
                "迁移没有开始",
                window.FindControl<TextBlock>("ErrorMessageText")?.Text,
                StringComparison.Ordinal);
            Assert.NotNull(window.FindControl<Button>("DismissButton"));
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(500, frame.PixelSize.Width);
            Assert.Equal(350, frame.PixelSize.Height);

            string? capturePath = Environment.GetEnvironmentVariable(
                "SNAPBOARD_STORAGE_MIGRATION_ERROR_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                frame.Save(capturePath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task NonEmptyStorageTargetExplainsThatExistingContentIsProtected()
    {
        const string target = @"D:\ProgramData\ExistingFiles";
        FakeStorageManagementService storage = new()
        {
            ValidationResult = new StorageLocationValidationResult(
                false,
                target,
                "volume-2",
                AvailableBytes: 0,
                RequiredBytes: 0,
                StorageLocationValidationError.ExistingStorage,
                "target-not-empty"),
        };
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            storageManagementService: storage,
            storagePlatformService: new FakeStoragePlatformService(),
            requestStorageMigration: static (_, _) => ValueTask.CompletedTask);

        await viewModel.SelectStorageTargetAsync(target);

        Assert.False(viewModel.CanConfirmStorageMigration);
        Assert.Contains("已有文件或子目录", viewModel.StorageStatus, StringComparison.Ordinal);
        Assert.Contains("空目录", viewModel.StorageStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoStartToggleUpdatesAvailableService()
    {
        FakeAutoStartService autoStart = new();
        SettingsViewModel viewModel = new(new FakeGlobalHotKeyService(), autoStart);

        viewModel.IsAutoStartEnabled = true;

        Assert.True(autoStart.IsEnabled());
        Assert.Equal(1, autoStart.SetCount);
        Assert.Equal("已启用登录启动", viewModel.AutoStartStatus);
    }

    [Fact]
    public void DevelopmentExecutableDisablesAutoStartWithoutWritingPlatformState()
    {
        FakeAutoStartService autoStart = new(AutoStartAvailability.RequiresAppBundle);
        SettingsViewModel viewModel = new(new FakeGlobalHotKeyService(), autoStart);

        viewModel.IsAutoStartEnabled = true;

        Assert.False(viewModel.IsAutoStartEnabled);
        Assert.False(viewModel.IsAutoStartAvailable);
        Assert.Equal(0, autoStart.SetCount);
        Assert.Equal("开发裸程序不支持；正式 App Bundle 可启用", viewModel.AutoStartStatus);
    }

    [Fact]
    public async Task SyncConfigurationClearsSensitiveFieldsAfterCreatingSpace()
    {
        FakeSyncService syncService = new();
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            syncService: syncService)
        {
            SyncEndpoint = "https://dav.example.test/user/",
            SyncRemoteRoot = "SnapBoard/v1",
            SyncUsername = "alice",
            SyncPassword = "app-password",
            SyncRecoveryCode = "correct horse battery staple",
        };

        await viewModel.ConfigureSyncCommand.ExecuteAsync(null);

        Assert.Equal(1, syncService.CreateCount);
        Assert.Equal(new Uri("https://dav.example.test/user/"), syncService.Configuration?.Endpoint);
        Assert.Equal("SnapBoard/v1", syncService.Configuration?.RemoteRoot);
        Assert.Equal("alice", syncService.Configuration?.Username);
        Assert.Equal("app-password"u8.ToArray(), syncService.Password);
        Assert.Equal("correct horse battery staple"u8.ToArray(), syncService.RecoveryCode);
        Assert.Empty(viewModel.SyncPassword);
        Assert.Empty(viewModel.SyncRecoveryCode);
        Assert.True(viewModel.IsSyncConfigured);
        Assert.Equal(syncService.SpaceId.ToString("D"), viewModel.SyncActiveSpaceId);
        Assert.True(viewModel.HasSyncRecoveryMaterial);
    }

    [Fact]
    public async Task HistorySettingsSaveUsesSelectedCaptureTypesAndRetentionPeriod()
    {
        FakeHistorySettingsService settings = new();
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            historySettingsService: settings);
        await viewModel.InitializeHistorySettingsAsync();
        viewModel.IsCaptureImagesEnabled = false;
        viewModel.IsCaptureFilesEnabled = false;
        viewModel.IsRetentionEnabled = true;
        viewModel.SelectedRetentionPeriod = Assert.Single(
            viewModel.RetentionPeriodOptions,
            option => option.Days == 90);

        await viewModel.SaveHistorySettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, settings.UpdateCount);
        Assert.Equal(
            new HistoryCaptureSettings(Text: true, RichText: true, Images: false, Files: false),
            settings.Current.Capture);
        Assert.Equal(new HistoryRetentionSettings(Enabled: true, RetentionDays: 90),
            settings.Current.Retention);
        Assert.Contains("保留 90 天", viewModel.HistorySettingsStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncFrequencyUsesDefaultWhenUnsetAndAppliesSelectedValue()
    {
        FakeSyncService syncService = new();
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            syncService: syncService);

        await viewModel.InitializeSyncAsync();

        Assert.Equal(5 * 60, viewModel.SelectedSyncFrequency.IntervalSeconds);
        viewModel.SelectedSyncFrequency = Assert.Single(
            viewModel.SyncFrequencyOptions,
            option => option.IntervalSeconds == 15 * 60);
        await viewModel.SaveSyncFrequencyCommand.ExecuteAsync(null);

        Assert.Equal(1, syncService.PollingSettingsUpdateCount);
        Assert.Equal(15 * 60, syncService.PollingSettings.PollIntervalSeconds);
        Assert.Contains("15 分钟", viewModel.SyncFrequencyStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderMigrationUsesLocalCredentialsAndClearsPassword()
    {
        FakeSyncService syncService = new(configured: true);
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            syncService: syncService);
        await viewModel.InitializeSyncAsync();
        viewModel.ProviderMigrationTargetEndpoint = "https://new.example.test/dav/";
        viewModel.ProviderMigrationTargetRoot = "SnapBoard/v2";
        viewModel.ProviderMigrationTargetUsername = "target-user";
        viewModel.ProviderMigrationTargetPassword = "target-secret";

        await viewModel.StartOrProvideProviderMigrationCommand.ExecuteAsync(null);

        Assert.Equal(1, syncService.ProviderMigrationStartCount);
        Assert.Equal(
            new Uri("https://new.example.test/dav/"),
            syncService.ProviderMigrationRequest?.TargetConfiguration.Endpoint);
        Assert.Equal("target-user", syncService.ProviderMigrationRequest?
            .TargetConfiguration.Username);
        Assert.Equal("target-secret"u8.ToArray(), syncService.ProviderMigrationPassword);
        Assert.Empty(viewModel.ProviderMigrationTargetPassword);
        Assert.True(viewModel.IsProviderMigrationContinueVisible);
        Assert.True(viewModel.IsProviderMigrationCancelVisible);
        Assert.Equal(2, viewModel.ProviderMigrationDevices.Count);

        await viewModel.ContinueProviderMigrationCommand.ExecuteAsync(null);

        Assert.Equal(1, syncService.ProviderMigrationContinueCount);
        Assert.Equal("https://new.example.test/dav/", viewModel.ProviderMigrationCurrentEndpoint);
        Assert.Contains("旧服务数据仍保留", viewModel.ProviderMigrationStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderMigrationParticipantProvidesItsOwnCredentials()
    {
        FakeSyncService syncService = new(configured: true);
        Guid planId = Guid.NewGuid();
        syncService.SetProviderMigration(new SyncProviderMigrationSnapshot(
            SyncProviderMigrationState.TargetCredentialsRequired,
            planId,
            syncService.SpaceId,
            Epoch: 1,
            SourceEndpoint: "https://old.example.test/dav/",
            SourceRemoteRoot: "SnapBoard/v1",
            TargetEndpoint: "https://new.example.test/dav/",
            TargetRemoteRoot: "SnapBoard/v2"));
        using SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            syncService: syncService);
        viewModel.ProviderMigrationTargetUsername = "device-two";
        viewModel.ProviderMigrationTargetPassword = "device-two-secret";

        await viewModel.StartOrProvideProviderMigrationCommand.ExecuteAsync(null);

        Assert.Equal(1, syncService.ProviderMigrationProvideCount);
        Assert.Equal(planId, syncService.LastProviderMigrationPlanId);
        Assert.Equal("device-two-secret"u8.ToArray(), syncService.ProviderMigrationPassword);
        Assert.Empty(viewModel.ProviderMigrationTargetPassword);
    }

    [AvaloniaFact]
    public async Task ProviderMigrationConfirmationIsOwnedAndCancelReturnsFalse()
    {
        Window owner = new() { Width = 700, Height = 720 };
        ProviderMigrationConfirmationWindow dialog = new(
            ProviderMigrationConfirmationAction.FreezeAndContinue);
        try
        {
            owner.Show();
            Task<bool> result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(dialog, owner.OwnedWindows);
            Assert.Equal(new Avalonia.Size(520, 330), dialog.ClientSize);
            Button cancel = Assert.IsType<Button>(dialog.FindControl<Button>("CancelButton"));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(await result);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }

            owner.Close();
        }
    }

    private static SettingsWindow CreateSettingsWindow(
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService) => new()
        {
            DataContext = new SettingsViewModel(hotKeyService, autoStartService),
        };

    private sealed class FakeGlobalHotKeyService : IGlobalHotKeyService, ITwoSlotGlobalHotKeyService
    {
        public event EventHandler? Pressed
        {
            add { }
            remove { }
        }

        public GlobalHotKeyGesture? CurrentGesture { get; private set; }

        public GlobalHotKeyGesture ConfiguredGesture { get; private set; } =
            GlobalHotKeyGesture.Default;

        public GlobalHotKeyGesture? CurrentDoubleGesture { get; private set; }

        public GlobalHotKeyGesture? ConfiguredDoubleGesture { get; private set; }

        public GlobalHotKeyRegistrationStatus NextDoubleRegistrationStatus { get; set; } =
            GlobalHotKeyRegistrationStatus.Registered;

        public event EventHandler<GlobalHotKeyTriggeredEventArgs>? Triggered
        {
            add { }
            remove { }
        }

        public TimeSpan DoubleTriggerInterval => TimeSpan.FromMilliseconds(400);

        public GlobalHotKeyGesture DefaultGesture => GlobalHotKeyGesture.WindowsDefault;

        public string ModifierDisplayNames => "Ctrl、Alt、Shift 或 Win";

        public GlobalHotKeyGestureCreationResult CreateGesture(
            GlobalHotKeyModifiers modifiers,
            string keyName) => CreateGestureCore(
            modifiers,
            keyName,
            requireModifier: true);

        public GlobalHotKeyGestureCreationResult CreateGesture(
            GlobalHotKeySlot slot,
            GlobalHotKeyModifiers modifiers,
            string keyName) => Enum.IsDefined(slot)
            ? CreateGestureCore(
                modifiers,
                keyName,
                requireModifier: slot == GlobalHotKeySlot.Primary)
            : new GlobalHotKeyGestureCreationResult(
                GlobalHotKeyGestureCreationStatus.UnsupportedKey);

        private static GlobalHotKeyGestureCreationResult CreateGestureCore(
            GlobalHotKeyModifiers modifiers,
            string keyName,
            bool requireModifier)
        {
            GlobalHotKeyModifiers userModifiers = modifiers &
                (GlobalHotKeyModifiers.Control |
                 GlobalHotKeyModifiers.Alt |
                 GlobalHotKeyModifiers.Shift |
                 GlobalHotKeyModifiers.Windows);
            if (requireModifier && userModifiers == GlobalHotKeyModifiers.None)
            {
                return new GlobalHotKeyGestureCreationResult(
                    GlobalHotKeyGestureCreationStatus.MissingModifier);
            }

            if (keyName.Length != 1 || keyName[0] is < 'A' or > 'Z')
            {
                return new GlobalHotKeyGestureCreationResult(
                    GlobalHotKeyGestureCreationStatus.UnsupportedKey);
            }

            List<string> displayParts = [];
            if (userModifiers.HasFlag(GlobalHotKeyModifiers.Control))
            {
                displayParts.Add("Ctrl");
            }

            if (userModifiers.HasFlag(GlobalHotKeyModifiers.Alt))
            {
                displayParts.Add("Alt");
            }

            if (userModifiers.HasFlag(GlobalHotKeyModifiers.Shift))
            {
                displayParts.Add("Shift");
            }

            if (userModifiers.HasFlag(GlobalHotKeyModifiers.Windows))
            {
                displayParts.Add("Win");
            }

            displayParts.Add(keyName);
            GlobalHotKeyGesture gesture = new(
                userModifiers | GlobalHotKeyModifiers.NoRepeat,
                keyName[0],
                string.Join('+', displayParts));
            return new GlobalHotKeyGestureCreationResult(
                GlobalHotKeyGestureCreationStatus.Created,
                gesture);
        }

        public ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
            GlobalHotKeyGesture gesture,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentGesture = gesture;
            ConfiguredGesture = gesture;
            return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                GlobalHotKeyRegistrationStatus.Registered));
        }

        public GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot) =>
            slot == GlobalHotKeySlot.Primary ? CurrentGesture : CurrentDoubleGesture;

        public GlobalHotKeyGesture? GetConfiguredGesture(GlobalHotKeySlot slot) =>
            slot == GlobalHotKeySlot.Primary ? ConfiguredGesture : ConfiguredDoubleGesture;

        public ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
            GlobalHotKeySlot slot,
            GlobalHotKeyGesture gesture,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot == GlobalHotKeySlot.Primary)
            {
                return RegisterAsync(gesture, cancellationToken);
            }

            if (gesture.HasSameBinding(ConfiguredGesture))
            {
                return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Duplicate));
            }

            GlobalHotKeyRegistrationStatus status = NextDoubleRegistrationStatus;
            NextDoubleRegistrationStatus = GlobalHotKeyRegistrationStatus.Registered;
            if (status == GlobalHotKeyRegistrationStatus.Registered)
            {
                CurrentDoubleGesture = gesture;
                ConfiguredDoubleGesture = gesture;
            }

            return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(status));
        }

        public ValueTask<GlobalHotKeyRegistrationResult> ClearAsync(
            GlobalHotKeySlot slot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot == GlobalHotKeySlot.Primary)
            {
                CurrentGesture = null;
            }
            else
            {
                CurrentDoubleGesture = null;
                ConfiguredDoubleGesture = null;
            }

            return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                GlobalHotKeyRegistrationStatus.Registered));
        }

        public ValueTask UnregisterAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentGesture = null;
            CurrentDoubleGesture = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDesktopLocalSettingsService : IDesktopLocalSettingsService
    {
        public event EventHandler<DesktopLocalSettingsChangedEventArgs>? Changed;

        public DesktopLocalSettings Current { get; private set; } =
            DesktopLocalSettings.CreateDefaults(GlobalHotKeyGesture.Default);

        public int UpdateCount { get; private set; }

        public DesktopLocalSettingsUpdateResult Update(DesktopLocalSettings settings)
        {
            Current = settings;
            UpdateCount++;
            Changed?.Invoke(this, new DesktopLocalSettingsChangedEventArgs(settings));
            return new DesktopLocalSettingsUpdateResult(Persisted: true);
        }

        public DesktopLocalSettingsUpdateResult Update(
            Func<DesktopLocalSettings, DesktopLocalSettings> update) => Update(update(Current));
    }

    private sealed class FakeForegroundWindowStateService : IPlatformForegroundWindowStateService
    {
        public ForegroundWindowStateResult Result { get; set; } = new(
            ForegroundWindowState.Normal,
            IsSnapBoard: false,
            Identity: null,
            ForegroundWindowDiagnosticCode.None);

        public ForegroundWindowStateResult GetForegroundWindowState() => Result;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        private bool _enabled;

        public FakeAutoStartService(
            AutoStartAvailability availability = AutoStartAvailability.Available)
        {
            Availability = availability;
        }

        public AutoStartAvailability Availability { get; }

        public int SetCount { get; private set; }

        public bool IsEnabled() => _enabled;

        public AutoStartUpdateResult SetEnabled(bool enabled)
        {
            SetCount++;
            _enabled = enabled;
            return new AutoStartUpdateResult(AutoStartUpdateStatus.Updated);
        }
    }

    private sealed class FakeHistorySettingsService : IHistorySettingsService
    {
        public event EventHandler<HistorySettingsChangedEvent>? Changed;

        public HistorySettingsSnapshot Current { get; private set; } =
            HistorySettingsSnapshot.Default;

        public int UpdateCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask UpdateAsync(
            HistoryCaptureSettings capture,
            HistoryRetentionSettings retention,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCount++;
            Current = new HistorySettingsSnapshot(capture, retention);
            Changed?.Invoke(this, new HistorySettingsChangedEvent(
                Current,
                HistorySettingKeys.Retention));
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplyRemoteSettingAsync(
            string key,
            string value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask PublishCurrentSettingsAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<int> ApplyRetentionNowAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }

    private sealed class FakeStorageManagementService : IStorageManagementService
    {
        public StorageLocationValidationResult ValidationResult { get; init; } =
            new(
                true,
                string.Empty,
                "volume-1",
                16L * 1024 * 1024 * 1024,
                64L * 1024 * 1024,
                StorageLocationValidationError.None);

        public ValueTask<StorageLocationSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new StorageLocationSnapshot(
                @"C:\SnapBoardData",
                @"C:\Users\tester\AppData\Local\SnapBoard\data",
                "storage-1234567890123456",
                "volume-1",
                new StorageUsage(1024 * 1024, 2 * 1024 * 1024, 4096),
                RollbackDirectory: null,
                StorageMigrationPhase.None,
                MigrationId: null,
                LastErrorCode: null));
        }

        public ValueTask<StorageLocationValidationResult> ValidateTargetAsync(
            string targetDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ValidationResult with
            {
                CanonicalTargetDirectory = string.IsNullOrEmpty(
                    ValidationResult.CanonicalTargetDirectory)
                    ? targetDirectory
                    : ValidationResult.CanonicalTargetDirectory,
            });
        }

        public ValueTask<StorageMigrationLaunchPlan> PrepareMigrationAsync(
            string targetDirectory,
            StorageProcessIdentity mainProcess,
            string mainExecutablePath,
            string migratorExecutablePath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AcknowledgeStartupAsync(
            string migrationId,
            StorageProcessIdentity process,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CancelPreparedMigrationAsync(
            string migrationId,
            string errorCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeStoragePlatformService : IStoragePlatformService
    {
        public ValueTask<StoragePathInspection> InspectPathAsync(
            string path,
            bool probeWriteCapabilities,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask EnsurePrivateDirectoryAsync(
            string path,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public StorageProcessIdentity GetCurrentProcessIdentity() =>
            throw new NotSupportedException();

        public ValueTask WaitForProcessExitAsync(
            StorageProcessIdentity process,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<StorageProcessIdentity> StartProcessAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask StopProcessAsync(
            StorageProcessIdentity process,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool OpenDirectory(string path) => true;
    }

    private sealed class FakeSyncService : ISyncService, ISyncProviderMigrationService
    {
        private SyncStatusSnapshot _status;

        public FakeSyncService(bool configured = false)
        {
            _status = configured
                ? new SyncStatusSnapshot(SyncServiceState.Idle, SpaceId)
                : new SyncStatusSnapshot(SyncServiceState.NotConfigured);
            ProviderMigration = new SyncProviderMigrationSnapshot(
                SyncProviderMigrationState.None,
                SpaceId: configured ? SpaceId : null,
                SourceEndpoint: configured ? "https://old.example.test/dav/" : null,
                SourceRemoteRoot: configured ? "SnapBoard/v1" : null);
        }

        public event EventHandler<SyncStatusSnapshot>? StatusChanged;

        public event EventHandler<SyncPollingSettingsChangedEvent>? PollingSettingsChanged;

        public event EventHandler<SyncProviderMigrationSnapshot>? ProviderMigrationChanged;

        public Guid SpaceId { get; } = Guid.NewGuid();

        public SyncStatusSnapshot Status => _status;

        public SyncPollingSettings PollingSettings { get; private set; } =
            SyncPollingSettings.Default;

        public int PollingSettingsUpdateCount { get; private set; }

        public int CreateCount { get; private set; }

        public SyncRemoteConfiguration? Configuration { get; private set; }

        public byte[] Password { get; private set; } = [];

        public byte[] RecoveryCode { get; private set; } = [];

        public SyncProviderMigrationSnapshot ProviderMigration { get; private set; }

        public int ProviderMigrationStartCount { get; private set; }

        public int ProviderMigrationProvideCount { get; private set; }

        public int ProviderMigrationContinueCount { get; private set; }

        public SyncProviderMigrationRequest? ProviderMigrationRequest { get; private set; }

        public byte[] ProviderMigrationPassword { get; private set; } = [];

        public Guid? LastProviderMigrationPlanId { get; private set; }

        public void Start()
        {
        }

        public bool RequestSync() => true;

        public ValueTask InitializePollingSettingsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask UpdatePollingSettingsAsync(
            SyncPollingSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PollingSettingsUpdateCount++;
            PollingSettings = settings;
            PollingSettingsChanged?.Invoke(
                this,
                new SyncPollingSettingsChangedEvent(settings));
            return ValueTask.CompletedTask;
        }

        public ValueTask<SyncStatusSnapshot> SynchronizeNowAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_status);
        }

        public ValueTask<SyncSetupResult> CreateSpaceAsync(
            SyncSetupRequest request,
            ReadOnlyMemory<byte> password,
            ReadOnlyMemory<byte> recoveryCode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            Configuration = request.RemoteConfiguration;
            Password = password.ToArray();
            RecoveryCode = recoveryCode.ToArray();
            _status = new SyncStatusSnapshot(SyncServiceState.Idle, SpaceId);
            StatusChanged?.Invoke(this, _status);
            return ValueTask.FromResult(new SyncSetupResult(
                SyncSetupStatus.Success,
                SpaceId,
                Guid.NewGuid(),
                @"C:\SnapBoardData\recovery\space.recovery"));
        }

        public ValueTask<SyncSetupResult> JoinSpaceAsync(
            Guid spaceId,
            int keyVersion,
            SyncSetupRequest request,
            ReadOnlyMemory<byte> password,
            ReadOnlyMemory<byte> recoveryEnvelope,
            ReadOnlyMemory<byte> recoveryCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask PauseAndDrainAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ResumeAfterPause()
        {
        }

        public ValueTask<SyncProviderMigrationSnapshot> RefreshProviderMigrationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ProviderMigration);
        }

        public ValueTask<SyncProviderMigrationResult> StartProviderMigrationAsync(
            SyncProviderMigrationRequest request,
            ReadOnlyMemory<byte> targetPassword,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderMigrationStartCount++;
            ProviderMigrationRequest = request;
            ProviderMigrationPassword = targetPassword.ToArray();
            Guid planId = Guid.NewGuid();
            SetProviderMigration(CreateWaitingSnapshot(planId, request));
            return ValueTask.FromResult(new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.WaitingForDevices,
                ProviderMigration));
        }

        public ValueTask<SyncProviderMigrationResult> ProvideProviderMigrationCredentialsAsync(
            Guid planId,
            SyncProviderMigrationRequest request,
            ReadOnlyMemory<byte> targetPassword,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderMigrationProvideCount++;
            LastProviderMigrationPlanId = planId;
            ProviderMigrationRequest = request;
            ProviderMigrationPassword = targetPassword.ToArray();
            SetProviderMigration(CreateWaitingSnapshot(planId, request));
            return ValueTask.FromResult(new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.WaitingForDevices,
                ProviderMigration));
        }

        public ValueTask<SyncProviderMigrationResult> ContinueProviderMigrationAsync(
            Guid planId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderMigrationContinueCount++;
            LastProviderMigrationPlanId = planId;
            SetProviderMigration(ProviderMigration with
            {
                State = SyncProviderMigrationState.Completed,
                CompletedObjects = ProviderMigration.TotalObjects,
                CompletedBytes = ProviderMigration.TotalBytes,
                OldRemoteRetained = true,
            });
            return ValueTask.FromResult(new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.Success,
                ProviderMigration));
        }

        public ValueTask<SyncProviderMigrationResult> CancelOrRollbackProviderMigrationAsync(
            Guid planId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastProviderMigrationPlanId = planId;
            SetProviderMigration(ProviderMigration with
            {
                State = SyncProviderMigrationState.RolledBack,
            });
            return ValueTask.FromResult(new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.Success,
                ProviderMigration));
        }

        public void SetProviderMigration(SyncProviderMigrationSnapshot snapshot)
        {
            ProviderMigration = snapshot;
            ProviderMigrationChanged?.Invoke(this, snapshot);
        }

        private SyncProviderMigrationSnapshot CreateWaitingSnapshot(
            Guid planId,
            SyncProviderMigrationRequest request) => new(
                SyncProviderMigrationState.WaitingForDeviceAcks,
                planId,
                SpaceId,
                Epoch: 1,
                SourceEndpoint: "https://old.example.test/dav/",
                SourceRemoteRoot: "SnapBoard/v1",
                TargetEndpoint: request.TargetConfiguration.Endpoint.AbsoluteUri,
                TargetRemoteRoot: request.TargetConfiguration.RemoteRoot,
                Devices:
                [
                    new SyncProviderMigrationDeviceSnapshot(
                        Guid.NewGuid(),
                        SyncProviderMigrationDeviceState.Ready,
                        4,
                        4),
                    new SyncProviderMigrationDeviceSnapshot(
                        Guid.NewGuid(),
                        SyncProviderMigrationDeviceState.Pending,
                        2,
                        2),
                ],
                TotalObjects: 8,
                TotalBytes: 4096);
    }

    private sealed class FakeApplicationUpdateService : IApplicationUpdateService
    {
        public event EventHandler<ApplicationUpdateStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public ApplicationUpdateSettings Settings => ApplicationUpdateSettings.Default;

        public ApplicationUpdateStatus Status { get; } = new(
            ApplicationUpdateState.UpdateAvailable,
            "1.0.0",
            "1.1.0",
            ActiveSource: "GitHub");

        public bool IsOfficialSourceConfigured => false;

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Start()
        {
        }

        public ValueTask UpdateSettingsAsync(
            ApplicationUpdateSettings settings,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask CheckForUpdatesAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DownloadUpdateAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void ScheduleInstallAndRestart()
        {
        }

        public void Dispose()
        {
        }
    }
}
