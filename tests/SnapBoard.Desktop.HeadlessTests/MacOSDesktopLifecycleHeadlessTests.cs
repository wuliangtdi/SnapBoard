using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Application.Sync;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class MacOSDesktopLifecycleHeadlessTests
{
    [AvaloniaFact]
    public void StartupWithoutDoubleGestureRegistersOnlyPrimary()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: false);

        context.Initialize();

        Assert.Equal([GlobalHotKeySlot.Primary], context.HotKey.RegisteredSlots);
    }

    [AvaloniaFact]
    public void GlobalProtectionSuppressesBothSlotsButExplicitRequestsAlwaysOpen()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: true);
        context.Initialize();
        context.Foreground.Result = Protected(ForegroundWindowState.FullScreen);

        context.HotKey.Raise(GlobalHotKeySlot.Primary);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        Assert.False(context.Coordinator.HasQuickWindow);

        context.Menu.RaiseShowQuick();
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);

        context.Coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.CloseWindows);
        Dispatcher.UIThread.RunJobs();
        context.MainViewModel.OpenQuickWindowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);

        context.Coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.CloseWindows);
        Dispatcher.UIThread.RunJobs();
        context.Coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.ShowQuickWindow);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);
    }

    [AvaloniaFact]
    public void MaximizedForegroundAllowsBothSlotsByDefault()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: true);
        context.Initialize();
        context.Foreground.Result = Protected(ForegroundWindowState.Maximized);

        context.HotKey.Raise(GlobalHotKeySlot.Primary);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);

        context.Coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.CloseWindows);
        Dispatcher.UIThread.RunJobs();
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);
    }

    [AvaloniaFact]
    public void StrictScopeSuppressesBothSlotsForMaximizedForeground()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: true);
        context.Initialize();
        context.LocalSettings.Update(context.LocalSettings.Current with
        {
            ProtectionScope = ForegroundProtectionScope.FullScreenAndMaximized,
        });
        context.Foreground.Result = Protected(ForegroundWindowState.Maximized);

        context.HotKey.Raise(GlobalHotKeySlot.Primary);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();

        Assert.False(context.Coordinator.HasQuickWindow);
    }

    [AvaloniaFact]
    public void EnteringProtectionCancelsPendingDoubleSequence()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: true);
        context.Initialize();

        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        context.Foreground.Result = Protected(ForegroundWindowState.FullScreen);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        Assert.False(context.Coordinator.HasQuickWindow);

        context.Foreground.Result = Normal();
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        Assert.False(context.Coordinator.HasQuickWindow);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);
    }

    [AvaloniaFact]
    public void PrimaryAndCompleteDoubleSequenceUseTheSameSingleQuickWindowFlow()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: true);
        context.Initialize();

        context.HotKey.Raise(GlobalHotKeySlot.Primary);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);
        context.Coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.CloseWindows);
        Dispatcher.UIThread.RunJobs();

        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        Assert.False(context.Coordinator.HasQuickWindow);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);
    }

    [AvaloniaFact]
    public void RepeatedCarbonPressesCannotCompleteDoubleSequence()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: true);
        context.Initialize();

        context.HotKey.Raise(GlobalHotKeySlot.Double);
        context.HotKey.Raise(GlobalHotKeySlot.Double, isRepeat: true);
        context.HotKey.Raise(GlobalHotKeySlot.Double, isRepeat: true);
        Dispatcher.UIThread.RunJobs();

        Assert.False(context.Coordinator.HasQuickWindow);
    }

    [AvaloniaFact]
    public void ManualPauseSurvivesForegroundProtectionAndMenuKeepsReasonsSeparate()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: false);
        context.Initialize();

        context.Capture.SetPauseReason(ClipboardCapturePauseReason.Manual, active: true);
        context.Capture.SetPauseReason(
            ClipboardCapturePauseReason.ForegroundProtection,
            active: true);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Menu.LastPausedState);
        Assert.True(context.Menu.LastForegroundProtectedState);

        context.Capture.SetPauseReason(
            ClipboardCapturePauseReason.ForegroundProtection,
            active: false);
        Dispatcher.UIThread.RunJobs();

        Assert.True(context.Capture.IsManuallyPaused);
        Assert.True(context.Menu.LastPausedState);
        Assert.False(context.Menu.LastForegroundProtectedState);
    }

    [AvaloniaFact]
    public void MenuCommandsCloseReleaseAndRecreateEveryWindow()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        FakeDesktopLifetime desktop = new();
        FakeClipboardPort clipboard = new();
        FakeDesktopLocalSettingsService localSettings = new();
        FakeForegroundWindowStateService foreground = new();
        using ClipboardCaptureCoordinator capture = new(
            clipboard,
            clipboard,
            captureService: null,
            foreground,
            localSettings);
        FakeGlobalHotKeyService hotKey = new();
        FakeAutoStartService autoStart = new();
        FakeAccessibilityPermissionService permission = new();
        FakePlacementService placement = new();
        FakeMenuBarService menu = new();
        List<string> migrationOperations = [];
        FakeStorageManagementService storage = new(migrationOperations);
        FakeStoragePlatformService storagePlatform = new(migrationOperations);
        FakeSyncService sync = new(migrationOperations);
        FakeHistorySettingsService historySettings = new();
        FakeDesktopSystemEventService systemEvents = new();
        MacOSDesktopLifecycleCoordinator coordinator = new(
            desktop,
            new MainViewModel(),
            clipboard,
            clipboard,
            hotKey,
            autoStart,
            permission,
            placement,
            menu,
            capture,
            null,
            storage,
            new FakeStorageMigrationBarrier(migrationOperations),
            storagePlatform,
            sync,
            historySettings,
            systemEvents,
            applicationUpdateService: null,
            foreground,
            localSettings);

        try
        {
            coordinator.Initialize(DesktopStartupMode.Background);
            Dispatcher.UIThread.RunJobs();
            Assert.False(coordinator.HasMainWindow);
            Assert.True(systemEvents.Started);

            systemEvents.RaiseSystemResumed();
            systemEvents.RaiseNetworkChanged();
            Assert.Equal(2, sync.RequestSyncCount);

            menu.RaiseShowMain();
            Dispatcher.UIThread.RunJobs();
            Assert.True(coordinator.HasMainWindow);

            menu.RaiseShowQuick();
            Dispatcher.UIThread.RunJobs();
            Assert.True(coordinator.HasQuickWindow);
            Assert.Equal(2, clipboard.CaptureTargetCount);
            Assert.Equal(2, placement.CaptureScreenCount);

            menu.RaiseShowSettings();
            Dispatcher.UIThread.RunJobs();
            Assert.True(coordinator.HasSettingsWindow);
            SettingsViewModel settings = Assert.IsType<SettingsViewModel>(
                coordinator.CurrentSettingsViewModel);
            Assert.True(settings.IsPermissionSectionVisible);
            Assert.True(settings.IsStorageSectionVisible);
            Assert.True(settings.IsHistorySettingsSectionVisible);
            Assert.True(settings.IsSyncSectionVisible);
            Assert.True(settings.IsRestrictedMode);
            Assert.True(settings.IsDoubleHotKeyAvailable);
            Assert.True(settings.IsFullScreenProtectionAvailable);
            Assert.DoesNotContain("Windows", settings.HotKeyStatus, StringComparison.OrdinalIgnoreCase);
            Assert.True(coordinator.SettingsWindowHasOwner);
            Assert.Equal(1, historySettings.SubscriberCount);

            coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.CloseWindows);
            Dispatcher.UIThread.RunJobs();
            Assert.False(coordinator.HasMainWindow);
            Assert.False(coordinator.HasQuickWindow);
            Assert.False(coordinator.HasSettingsWindow);
            Assert.Equal(0, historySettings.SubscriberCount);

            menu.RaiseShowMain();
            menu.RaiseShowQuick();
            menu.RaiseShowSettings();
            Dispatcher.UIThread.RunJobs();
            Assert.True(coordinator.HasMainWindow);
            Assert.True(coordinator.HasQuickWindow);
            Assert.True(coordinator.HasSettingsWindow);
            Assert.Equal(1, historySettings.SubscriberCount);

            menu.RaiseTogglePause();
            Dispatcher.UIThread.RunJobs();
            Assert.True(capture.IsPaused);
            Assert.True(menu.LastPausedState);
            Assert.False(menu.LastForegroundProtectedState);
        }
        finally
        {
            coordinator.Dispose();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(menu.Disposed);
        Assert.True(hotKey.Unregistered);
        Assert.Equal(0, historySettings.SubscriberCount);
        Assert.True(systemEvents.Disposed);
        systemEvents.RaiseSystemResumed();
        systemEvents.RaiseNetworkChanged();
        Assert.Equal(2, sync.RequestSyncCount);
    }

    [AvaloniaFact]
    [SupportedOSPlatform("macos")]
    public async Task StorageMigrationPreparationUsesRequiredTransactionOrder()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        List<string> operations = [];
        FakeClipboardPort clipboard = new();
        using ClipboardCaptureCoordinator capture = new(clipboard, clipboard);
        capture.StateChanged += state =>
            operations.Add(state.IsPaused ? "capture-pause" : "capture-resume");
        FakeStorageManagementService storage = new(operations);
        FakeStoragePlatformService platform = new(operations);
        FakeSyncService sync = new(operations);
        using MacOSDesktopLifecycleCoordinator coordinator = CreateMigrationCoordinator(
            clipboard,
            capture,
            storage,
            platform,
            sync,
            new FakeStorageMigrationBarrier(operations));

        await coordinator.BeginStorageMigrationAsync("/tmp/snapboard-target", CancellationToken.None);

        Assert.Equal(
            ["prepare", "start-helper", "sync-pause", "capture-pause", "database-barrier"],
            operations);
        Assert.True(capture.IsPaused);
        Assert.False(sync.ResumeCalled);
    }

    [AvaloniaFact]
    [SupportedOSPlatform("macos")]
    public async Task FailedStorageMigrationPreparationStopsHelperAndRestoresServices()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        List<string> operations = [];
        FakeClipboardPort clipboard = new();
        using ClipboardCaptureCoordinator capture = new(clipboard, clipboard);
        capture.StateChanged += state =>
            operations.Add(state.IsPaused ? "capture-pause" : "capture-resume");
        FakeStorageManagementService storage = new(operations);
        FakeStoragePlatformService platform = new(operations);
        FakeSyncService sync = new(operations);
        using MacOSDesktopLifecycleCoordinator coordinator = CreateMigrationCoordinator(
            clipboard,
            capture,
            storage,
            platform,
            sync,
            new FakeStorageMigrationBarrier(operations, throwOnPrepare: true));

        await Assert.ThrowsAsync<IOException>(async () =>
            await coordinator.BeginStorageMigrationAsync(
                "/tmp/snapboard-target",
                CancellationToken.None));

        Assert.Equal(
            [
                "prepare",
                "start-helper",
                "sync-pause",
                "capture-pause",
                "database-barrier",
                "stop-helper",
                "cancel-prepared",
                "capture-resume",
                "sync-resume",
            ],
            operations);
        Assert.False(capture.IsPaused);
        Assert.True(sync.ResumeCalled);
    }

    [Fact]
    public void PermissionCommandsAreOnlyInvokedByExplicitViewModelActions()
    {
        FakeAccessibilityPermissionService permission = new();
        SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            permission);

        Assert.Equal(0, permission.RequestCount);
        Assert.Equal(0, permission.OpenSettingsCount);
        Assert.True(viewModel.IsRestrictedMode);

        viewModel.RequestAccessibilityPermissionCommand.Execute(null);
        viewModel.OpenAccessibilitySettingsCommand.Execute(null);

        Assert.Equal(1, permission.RequestCount);
        Assert.Equal(1, permission.OpenSettingsCount);
    }

    [Fact]
    public void GrantedPermissionDisplaysStableBundleIdentity()
    {
        FakeAccessibilityPermissionService permission = new()
        {
            State = new AccessibilityPermissionState(
                AccessibilityPermissionAccess.Granted,
                AccessibilityTrusted: true,
                EventPostingAllowed: true,
                ApplicationIdentityKind.AppBundle,
                "com.wuliangtdi.snapboard"),
        };
        SettingsViewModel viewModel = new(
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            permission);

        Assert.False(viewModel.IsRestrictedMode);
        Assert.Equal("已授权：可恢复目标应用并自动粘贴", viewModel.AccessibilityPermissionStatus);
        Assert.Equal(
            "App Bundle 身份：com.wuliangtdi.snapboard",
            viewModel.ApplicationIdentityStatus);
        Assert.Equal(0, permission.RequestCount);
    }

    [SupportedOSPlatform("macos")]
    private static MacOSDesktopLifecycleCoordinator CreateMigrationCoordinator(
        FakeClipboardPort clipboard,
        ClipboardCaptureCoordinator capture,
        IStorageManagementService storage,
        IStoragePlatformService platform,
        ISyncService sync,
        IStorageMigrationBarrier barrier) => new(
            new FakeDesktopLifetime(),
            new MainViewModel(),
            clipboard,
            clipboard,
            new FakeGlobalHotKeyService(),
            new FakeAutoStartService(),
            new FakeAccessibilityPermissionService(),
            new FakePlacementService(),
            new FakeMenuBarService(),
            capture,
            null,
            storage,
            barrier,
            platform,
            sync,
            new FakeHistorySettingsService(),
            foregroundWindowStateService: new FakeForegroundWindowStateService(),
            localSettings: new FakeDesktopLocalSettingsService());

    private sealed class FakeStorageManagementService(List<string> operations) :
        IStorageManagementService
    {
        public ValueTask<StorageLocationSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new StorageLocationSnapshot(
                "/tmp/snapboard-data",
                "/tmp/snapboard-default",
                "storage-1234567890123456",
                "volume-1",
                new StorageUsage(1024, 2048, 4096),
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
            return ValueTask.FromResult(new StorageLocationValidationResult(
                true,
                targetDirectory,
                "volume-1",
                4096,
                1024,
                StorageLocationValidationError.None));
        }

        public ValueTask<StorageMigrationLaunchPlan> PrepareMigrationAsync(
            string targetDirectory,
            StorageProcessIdentity mainProcess,
            string mainExecutablePath,
            string migratorExecutablePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Environment.ProcessPath, mainExecutablePath);
            Assert.EndsWith(
                Path.DirectorySeparatorChar + "SnapBoard.StorageMigrator",
                migratorExecutablePath,
                StringComparison.Ordinal);
            operations.Add("prepare");
            return ValueTask.FromResult(new StorageMigrationLaunchPlan(
                "migration-1",
                "/tmp/manifest.json",
                migratorExecutablePath,
                ["--manifest", "/tmp/manifest.json"]));
        }

        public ValueTask AcknowledgeStartupAsync(
            string migrationId,
            StorageProcessIdentity process,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CancelPreparedMigrationAsync(
            string migrationId,
            string errorCode,
            CancellationToken cancellationToken)
        {
            Assert.Equal("migration-1", migrationId);
            Assert.Equal("main-preparation-failed", errorCode);
            operations.Add("cancel-prepared");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStorageMigrationBarrier(
        List<string> operations,
        bool throwOnPrepare = false) : IStorageMigrationBarrier
    {
        public ValueTask PrepareForMigrationAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("database-barrier");
            return throwOnPrepare
                ? ValueTask.FromException(new IOException("database close failed"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStoragePlatformService(List<string> operations) :
        IStoragePlatformService
    {
        public ValueTask<StoragePathInspection> InspectPathAsync(
            string path,
            bool probeWriteCapabilities,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask EnsurePrivateDirectoryAsync(
            string path,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public StorageProcessIdentity GetCurrentProcessIdentity() => new(
            Environment.ProcessId,
            1,
            Environment.ProcessPath ?? throw new InvalidOperationException(),
            Environment.UserName);

        public ValueTask WaitForProcessExitAsync(
            StorageProcessIdentity process,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<StorageProcessIdentity> StartProcessAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("start-helper");
            return ValueTask.FromResult(new StorageProcessIdentity(
                4242,
                2,
                executablePath,
                Environment.UserName));
        }

        public ValueTask StopProcessAsync(
            StorageProcessIdentity process,
            CancellationToken cancellationToken)
        {
            operations.Add("stop-helper");
            return ValueTask.CompletedTask;
        }

        public bool OpenDirectory(string path) => true;
    }

    private sealed class FakeSyncService(List<string> operations) : ISyncService
    {
        public event EventHandler<SyncStatusSnapshot>? StatusChanged;

        public event EventHandler<SyncPollingSettingsChangedEvent>? PollingSettingsChanged;

        public SyncStatusSnapshot Status { get; } = new(SyncServiceState.NotConfigured);

        public SyncPollingSettings PollingSettings { get; private set; } =
            SyncPollingSettings.Default;

        public bool ResumeCalled { get; private set; }

        public int RequestSyncCount { get; private set; }

        public void Start()
        {
            StatusChanged?.Invoke(this, Status);
        }

        public bool RequestSync()
        {
            RequestSyncCount++;
            return true;
        }

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
            PollingSettings = settings;
            PollingSettingsChanged?.Invoke(this, new SyncPollingSettingsChangedEvent(settings));
            return ValueTask.CompletedTask;
        }

        public ValueTask<SyncStatusSnapshot> SynchronizeNowAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(Status);

        public ValueTask<SyncSetupResult> CreateSpaceAsync(
            SyncSetupRequest request,
            ReadOnlyMemory<byte> password,
            ReadOnlyMemory<byte> recoveryCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SyncSetupResult> JoinSpaceAsync(
            Guid spaceId,
            int keyVersion,
            SyncSetupRequest request,
            ReadOnlyMemory<byte> password,
            ReadOnlyMemory<byte> recoveryEnvelope,
            ReadOnlyMemory<byte> recoveryCode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask PauseAndDrainAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("sync-pause");
            return ValueTask.CompletedTask;
        }

        public void ResumeAfterPause()
        {
            ResumeCalled = true;
            operations.Add("sync-resume");
        }
    }

    private sealed class FakeDesktopSystemEventService : IDesktopSystemEventService
    {
        public event EventHandler? SystemResumed;

        public event EventHandler? NetworkChanged;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public void Start() => Started = true;

        public void Dispose() => Disposed = true;

        public void RaiseSystemResumed() => SystemResumed?.Invoke(this, EventArgs.Empty);

        public void RaiseNetworkChanged() => NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeHistorySettingsService : IHistorySettingsService
    {
        private EventHandler<HistorySettingsChangedEvent>? _changed;

        public event EventHandler<HistorySettingsChangedEvent>? Changed
        {
            add
            {
                _changed += value;
                SubscriberCount++;
            }
            remove
            {
                _changed -= value;
                SubscriberCount--;
            }
        }

        public HistorySettingsSnapshot Current { get; } = HistorySettingsSnapshot.Default;

        public int SubscriberCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            HistoryCaptureSettings capture,
            HistoryRetentionSettings retention,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ApplyRemoteSettingAsync(
            string key,
            string value,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask PublishCurrentSettingsAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<int> ApplyRetentionNowAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);
    }

    [AvaloniaFact]
    public void ConflictingPersistedHotKeyIsNotReplacedAtStartup()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        GlobalHotKeyGesture customGesture = new(
            GlobalHotKeyModifiers.Meta |
            GlobalHotKeyModifiers.Alt |
            GlobalHotKeyModifiers.NoRepeat,
            0,
            "Command+Option+A");
        FakeGlobalHotKeyService hotKey = new()
        {
            FailNextRegistration = true,
        };
        FakeDesktopLocalSettingsService localSettings = new();
        localSettings.Update(localSettings.Current with
        {
            PrimaryHotKey = customGesture,
        });
        FakeForegroundWindowStateService foreground = new();
        FakeDesktopLifetime desktop = new();
        FakeClipboardPort clipboard = new();
        using ClipboardCaptureCoordinator capture = new(
            clipboard,
            clipboard,
            captureService: null,
            foreground,
            localSettings);
        MainViewModel mainViewModel = new();
        MacOSDesktopLifecycleCoordinator coordinator = new(
            desktop,
            mainViewModel,
            clipboard,
            clipboard,
            hotKey,
            new FakeAutoStartService(),
            new FakeAccessibilityPermissionService(),
            new FakePlacementService(),
            new FakeMenuBarService(),
            capture,
            null,
            foregroundWindowStateService: foreground,
            localSettings: localSettings);

        try
        {
            coordinator.Initialize(DesktopStartupMode.Background);

            Assert.Equal(
                [customGesture],
                hotKey.RegistrationAttempts);
            Assert.Equal(customGesture, localSettings.Current.PrimaryHotKey);
            Assert.Contains("已被占用", mainViewModel.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            coordinator.Dispose();
        }
    }

    private static ForegroundWindowStateResult Normal() => new(
        ForegroundWindowState.Normal,
        IsSnapBoard: false,
        new ForegroundWindowIdentity(10, 20),
        ForegroundWindowDiagnosticCode.None);

    private static ForegroundWindowStateResult Protected(ForegroundWindowState state) => new(
        state,
        IsSnapBoard: false,
        new ForegroundWindowIdentity(10, 20),
        ForegroundWindowDiagnosticCode.None);

    [SupportedOSPlatform("macos")]
    private sealed class LifecycleContext : IDisposable
    {
        private readonly ClipboardCaptureCoordinator _capture;
        private readonly FakeDesktopLifetime _desktop = new();
        private int _disposed;

        public LifecycleContext(bool configureDoubleGesture)
        {
            if (configureDoubleGesture)
            {
                LocalSettings.Update(LocalSettings.Current with
                {
                    DoubleHotKey = new GlobalHotKeyGesture(
                        GlobalHotKeyModifiers.Control |
                        GlobalHotKeyModifiers.Alt |
                        GlobalHotKeyModifiers.NoRepeat,
                        0x28,
                        "Control+Option+K"),
                });
            }

            _capture = new ClipboardCaptureCoordinator(
                Clipboard,
                Clipboard,
                captureService: null,
                Foreground,
                LocalSettings);
            Coordinator = new MacOSDesktopLifecycleCoordinator(
                _desktop,
                MainViewModel,
                Clipboard,
                Clipboard,
                HotKey,
                new FakeAutoStartService(),
                new FakeAccessibilityPermissionService(),
                new FakePlacementService(),
                Menu,
                _capture,
                singleInstance: null,
                foregroundWindowStateService: Foreground,
                localSettings: LocalSettings);
        }

        public FakeClipboardPort Clipboard { get; } = new();

        public ClipboardCaptureCoordinator Capture => _capture;

        public MacOSDesktopLifecycleCoordinator Coordinator { get; }

        public FakeForegroundWindowStateService Foreground { get; } = new();

        public FakeGlobalHotKeyService HotKey { get; } = new();

        public FakeDesktopLocalSettingsService LocalSettings { get; } = new();

        public MainViewModel MainViewModel { get; } = new();

        public FakeMenuBarService Menu { get; } = new();

        public void Initialize()
        {
            Coordinator.Initialize(DesktopStartupMode.Background);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Coordinator.Dispose();
            Dispatcher.UIThread.RunJobs();
            _capture.Dispose();
        }
    }

    private sealed class FakeDesktopLifetime : IDesktopApplicationLifetime
    {
        public event EventHandler? ReopenRequested;

        public Avalonia.Controls.Window? MainWindow { get; set; }

        public bool UsesExplicitShutdown { get; private set; }

        public bool TryShutdown() => true;

        public void UseExplicitShutdown() => UsesExplicitShutdown = true;

        public void RaiseReopen() => ReopenRequested?.Invoke(this, EventArgs.Empty);

        public void Dispose() { }
    }

    private sealed class FakeClipboardPort :
        IClipboardMonitor,
        IClipboardContentReader,
        IClipboardWriter,
        IAutomaticPasteService
    {
        private readonly Channel<ClipboardChangedEvent> _events =
            Channel.CreateUnbounded<ClipboardChangedEvent>();

        public int CaptureTargetCount { get; private set; }

        public async IAsyncEnumerable<ClipboardChangedEvent> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (ClipboardChangedEvent change in
                _events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }

        public ValueTask<ClipboardReadResult> ReadAsync(
            ClipboardChangedEvent change,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new ClipboardReadResult(
                ClipboardReadStatus.Failed,
                null,
                ClipboardReadFailureReason.NativeFailure));

        public ValueTask<ClipboardWriteResult> WriteAsync(
            ClipboardWriteRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new ClipboardWriteResult(ClipboardWriteStatus.Success, 1, true));

        public ValueTask<ClipboardWriteResult> WritePlainTextAsync(
            string text,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new ClipboardWriteResult(ClipboardWriteStatus.Success, 1, true));

        public IAutomaticPasteTarget? CaptureForegroundTarget()
        {
            CaptureTargetCount++;
            return new FakePasteTarget();
        }

        public ValueTask<ForegroundActivationResult> TryActivateTargetAsync(
            IAutomaticPasteTarget target,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new ForegroundActivationResult(ForegroundActivationStatus.Activated));

        public ValueTask<AutomaticPasteResult> TryPasteAsync(
            IAutomaticPasteTarget target,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new AutomaticPasteResult(AutomaticPasteStatus.Pasted));

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePasteTarget : IAutomaticPasteTarget;

    private sealed class FakeGlobalHotKeyService :
        IGlobalHotKeyService,
        ITwoSlotGlobalHotKeyService
    {
        private readonly Dictionary<GlobalHotKeySlot, GlobalHotKeyGesture?> _gestures = new()
        {
            [GlobalHotKeySlot.Primary] = null,
            [GlobalHotKeySlot.Double] = null,
        };

        public event EventHandler? Pressed;

        public event EventHandler<GlobalHotKeyTriggeredEventArgs>? Triggered;

        public GlobalHotKeyGesture? CurrentGesture => _gestures[GlobalHotKeySlot.Primary];

        public GlobalHotKeyGesture ConfiguredGesture =>
            _gestures[GlobalHotKeySlot.Primary] ?? GlobalHotKeyGesture.MacOSDefault;

        public GlobalHotKeyGesture DefaultGesture => GlobalHotKeyGesture.MacOSDefault;

        public string ModifierDisplayNames => "Command、Option、Control 或 Shift";

        public TimeSpan DoubleTriggerInterval => TimeSpan.FromMilliseconds(400);

        public bool Unregistered { get; private set; }

        public bool FailNextRegistration { get; init; }

        public List<GlobalHotKeyGesture> RegistrationAttempts { get; } = [];

        public List<GlobalHotKeySlot> RegisteredSlots { get; } = [];

        public GlobalHotKeyGestureCreationResult CreateGesture(
            GlobalHotKeyModifiers modifiers,
            string keyName) => new(
            GlobalHotKeyGestureCreationStatus.Created,
            new GlobalHotKeyGesture(
                modifiers | GlobalHotKeyModifiers.NoRepeat,
                9,
                "Command+Shift+V"));

        public GlobalHotKeyGestureCreationResult CreateGesture(
            GlobalHotKeySlot slot,
            GlobalHotKeyModifiers modifiers,
            string keyName) => CreateGesture(modifiers, keyName);

        public GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot) =>
            _gestures[slot];

        public GlobalHotKeyGesture? GetConfiguredGesture(GlobalHotKeySlot slot) =>
            _gestures[slot];

        public ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
            GlobalHotKeyGesture gesture,
            CancellationToken cancellationToken) => RegisterAsync(
            GlobalHotKeySlot.Primary,
            gesture,
            cancellationToken);

        public ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
            GlobalHotKeySlot slot,
            GlobalHotKeyGesture gesture,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegistrationAttempts.Add(gesture);
            if (FailNextRegistration && RegistrationAttempts.Count == 1)
            {
                return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Conflict,
                    -9878));
            }

            _gestures[slot] = gesture;
            RegisteredSlots.Add(slot);
            return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                GlobalHotKeyRegistrationStatus.Registered));
        }

        public ValueTask<GlobalHotKeyRegistrationResult> ClearAsync(
            GlobalHotKeySlot slot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _gestures[slot] = null;
            return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                GlobalHotKeyRegistrationStatus.Registered));
        }

        public ValueTask UnregisterAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _gestures[GlobalHotKeySlot.Primary] = null;
            _gestures[GlobalHotKeySlot.Double] = null;
            Unregistered = true;
            return ValueTask.CompletedTask;
        }

        public void Raise(GlobalHotKeySlot slot, bool isRepeat = false)
        {
            Triggered?.Invoke(this, new GlobalHotKeyTriggeredEventArgs(slot, isRepeat));
            if (slot == GlobalHotKeySlot.Primary && !isRepeat)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDesktopLocalSettingsService : IDesktopLocalSettingsService
    {
        public event EventHandler<DesktopLocalSettingsChangedEventArgs>? Changed;

        public DesktopLocalSettings Current { get; private set; } =
            DesktopLocalSettings.CreateDefaults(GlobalHotKeyGesture.MacOSDefault);

        public DesktopLocalSettingsUpdateResult Update(DesktopLocalSettings settings)
        {
            Current = settings;
            Changed?.Invoke(this, new DesktopLocalSettingsChangedEventArgs(settings));
            return new DesktopLocalSettingsUpdateResult(Persisted: true);
        }

        public DesktopLocalSettingsUpdateResult Update(
            Func<DesktopLocalSettings, DesktopLocalSettings> update) => Update(update(Current));
    }

    private sealed class FakeForegroundWindowStateService :
        IPlatformForegroundWindowStateService
    {
        public ForegroundWindowStateResult Result { get; set; } = Normal();

        public ForegroundWindowStateResult GetForegroundWindowState() => Result;
    }

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public AutoStartAvailability Availability => AutoStartAvailability.RequiresAppBundle;

        public bool IsEnabled() => false;

        public AutoStartUpdateResult SetEnabled(bool enabled) =>
            new(AutoStartUpdateStatus.Unsupported);
    }

    private sealed class FakeAccessibilityPermissionService : IAccessibilityPermissionService
    {
        public int RequestCount { get; private set; }

        public int OpenSettingsCount { get; private set; }

        public AccessibilityPermissionState State { get; init; } = new(
            AccessibilityPermissionAccess.Denied,
            AccessibilityTrusted: false,
            EventPostingAllowed: false,
            ApplicationIdentityKind.DevelopmentExecutable,
            null);

        public AccessibilityPermissionState GetState() => State;

        public AccessibilityPermissionActionResult RequestAccess()
        {
            RequestCount++;
            return new AccessibilityPermissionActionResult(GetState(), true);
        }

        public bool OpenSystemSettings()
        {
            OpenSettingsCount++;
            return true;
        }
    }

    private sealed class FakePlacementService : IPlatformWindowPlacementService
    {
        public int CenterCount { get; private set; }

        public int CaptureScreenCount { get; private set; }

        public PlatformScreenPlacement? CaptureForegroundScreen()
        {
            CaptureScreenCount++;
            return new PlatformScreenPlacement(0, 0, 1440, 900, 192);
        }

        public bool CenterWindow(
            nint windowHandle,
            PlatformScreenPlacement screen,
            int widthInDeviceIndependentPixels,
            int heightInDeviceIndependentPixels)
        {
            CenterCount++;
            return true;
        }

        public bool TryRestore(nint windowHandle, string placementKey) => true;

        public void Save(nint windowHandle, string placementKey)
        {
        }

        public bool TryActivate(nint windowHandle) => true;
    }

    private sealed class FakeMenuBarService : IDesktopMenuBarService
    {
        public event EventHandler? ShowMainWindowRequested;

        public event EventHandler? ShowQuickWindowRequested;

        public event EventHandler? RecordingPauseToggleRequested;

        public event EventHandler? ShowSettingsWindowRequested;

        public event EventHandler? ExitRequested;

        public bool LastPausedState { get; private set; }

        public bool LastForegroundProtectedState { get; private set; }

        public bool LastInternallyPausedState { get; private set; }

        public bool Disposed { get; private set; }

        public void Initialize(bool recordingPaused) => LastPausedState = recordingPaused;

        public void SetRecordingPaused(bool paused) => LastPausedState = paused;

        public void SetRecordingState(
            bool manuallyPaused,
            bool foregroundProtected,
            bool internallyPaused)
        {
            LastPausedState = manuallyPaused;
            LastForegroundProtectedState = foregroundProtected;
            LastInternallyPausedState = internallyPaused;
        }

        public void RaiseShowMain() => ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseShowQuick() => ShowQuickWindowRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseTogglePause() => RecordingPauseToggleRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseShowSettings() => ShowSettingsWindowRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

        public void Dispose() => Disposed = true;
    }
}
