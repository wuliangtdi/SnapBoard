using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Application.Sync;
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
        SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            storageManagementService: new FakeStorageManagementService(),
            storagePlatformService: new FakeStoragePlatformService(),
            requestStorageMigration: static (_, _) => ValueTask.CompletedTask,
            syncService: new FakeSyncService(),
            historySettingsService: new FakeHistorySettingsService());
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

            Assert.Equal(new Avalonia.Size(700, 720), window.ClientSize);
            Assert.NotNull(window.Icon);
            Assert.NotNull(window.FindControl<Button>("HotKeyCaptureButton"));
            TextBox syncEndpoint = Assert.IsType<TextBox>(
                window.FindControl<TextBox>("SyncEndpointTextBox"));
            Button configureSync = Assert.IsType<Button>(
                window.FindControl<Button>("ConfigureSyncButton"));
            Button saveHistory = Assert.IsType<Button>(
                window.FindControl<Button>("SaveHistorySettingsButton"));
            Assert.IsType<ComboBox>(window.FindControl<ComboBox>("SyncFrequencyComboBox"));
            Assert.IsType<Button>(window.FindControl<Button>("SaveSyncFrequencyButton"));
            Assert.Equal(@"C:\SnapBoardData", viewModel.CurrentStorageDirectory);
            Assert.Contains("1 MiB", viewModel.StorageUsageText, StringComparison.Ordinal);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(700, frame.PixelSize.Width);
            Assert.Equal(720, frame.PixelSize.Height);

            string? capturePath =
                Environment.GetEnvironmentVariable("SNAPBOARD_SETTINGS_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
                frame.Save(capturePath, PngBitmapEncoderOptions.Default);
            }

            Assert.True(syncEndpoint.IsVisible);
            Assert.True(saveHistory.IsVisible);
            saveHistory.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            using var historyFrame = window.CaptureRenderedFrame();
            Assert.NotNull(historyFrame);
            string? historyCapturePath =
                Environment.GetEnvironmentVariable("SNAPBOARD_SETTINGS_HISTORY_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(historyCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyCapturePath)!);
                historyFrame.Save(historyCapturePath, PngBitmapEncoderOptions.Default);
            }

            configureSync.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            using var syncFrame = window.CaptureRenderedFrame();
            Assert.NotNull(syncFrame);
            Assert.Equal(700, syncFrame.PixelSize.Width);
            Assert.Equal(720, syncFrame.PixelSize.Height);
            string? syncCapturePath =
                Environment.GetEnvironmentVariable("SNAPBOARD_SETTINGS_SYNC_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(syncCapturePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(syncCapturePath)!);
                syncFrame.Save(syncCapturePath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
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
            Assert.Equal(700, frame.PixelSize.Width);
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

    private static SettingsWindow CreateSettingsWindow(
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService) => new()
        {
            DataContext = new SettingsViewModel(hotKeyService, autoStartService),
        };

    private sealed class FakeGlobalHotKeyService : IGlobalHotKeyService
    {
        public event EventHandler? Pressed
        {
            add { }
            remove { }
        }

        public GlobalHotKeyGesture? CurrentGesture { get; private set; }

        public GlobalHotKeyGesture ConfiguredGesture { get; private set; } =
            GlobalHotKeyGesture.Default;

        public GlobalHotKeyGesture DefaultGesture => GlobalHotKeyGesture.WindowsDefault;

        public string ModifierDisplayNames => "Ctrl、Alt、Shift 或 Win";

        public GlobalHotKeyGestureCreationResult CreateGesture(
            GlobalHotKeyModifiers modifiers,
            string keyName)
        {
            GlobalHotKeyModifiers userModifiers = modifiers &
                (GlobalHotKeyModifiers.Control |
                 GlobalHotKeyModifiers.Alt |
                 GlobalHotKeyModifiers.Shift |
                 GlobalHotKeyModifiers.Windows);
            if (userModifiers == GlobalHotKeyModifiers.None)
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

        public ValueTask UnregisterAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentGesture = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class FakeSyncService : ISyncService
    {
        private SyncStatusSnapshot _status = new(SyncServiceState.NotConfigured);

        public event EventHandler<SyncStatusSnapshot>? StatusChanged;

        public event EventHandler<SyncPollingSettingsChangedEvent>? PollingSettingsChanged;

        public Guid SpaceId { get; } = Guid.NewGuid();

        public SyncStatusSnapshot Status => _status;

        public SyncPollingSettings PollingSettings { get; private set; } =
            SyncPollingSettings.Default;

        public int PollingSettingsUpdateCount { get; private set; }

        public int CreateCount { get; private set; }

        public SyncRemoteConfiguration? Configuration { get; private set; }

        public byte[] Password { get; private set; } = [];

        public byte[] RecoveryCode { get; private set; } = [];

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
    }
}
