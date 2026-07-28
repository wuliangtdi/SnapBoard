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
    public void MenuCommandsCloseReleaseAndRecreateEveryWindow()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        FakeDesktopLifetime desktop = new();
        FakeClipboardPort clipboard = new();
        using ClipboardCaptureCoordinator capture = new(clipboard, clipboard);
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
            systemEvents);

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
            new FakeHistorySettingsService());

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
    public void ConflictingPersistedHotKeyFallsBackToDefaultAtStartup()
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
        FakeGlobalHotKeyService hotKey = new(customGesture)
        {
            FailNextRegistration = true,
        };
        FakeDesktopLifetime desktop = new();
        FakeClipboardPort clipboard = new();
        using ClipboardCaptureCoordinator capture = new(clipboard, clipboard);
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
            null);

        try
        {
            coordinator.Initialize(DesktopStartupMode.Background);

            Assert.Equal(
                [customGesture, GlobalHotKeyGesture.MacOSDefault],
                hotKey.RegistrationAttempts);
            Assert.Equal(GlobalHotKeyGesture.MacOSDefault, hotKey.ConfiguredGesture);
            Assert.Contains("已恢复默认快捷键", mainViewModel.StatusMessage, StringComparison.Ordinal);
        }
        finally
        {
            coordinator.Dispose();
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

    private sealed class FakeGlobalHotKeyService : IGlobalHotKeyService
    {
        public FakeGlobalHotKeyService()
        {
        }

        public FakeGlobalHotKeyService(GlobalHotKeyGesture configuredGesture)
        {
            ConfiguredGesture = configuredGesture;
        }

        public event EventHandler? Pressed;

        public GlobalHotKeyGesture? CurrentGesture { get; private set; }

        public GlobalHotKeyGesture ConfiguredGesture { get; private set; } =
            GlobalHotKeyGesture.MacOSDefault;

        public GlobalHotKeyGesture DefaultGesture => GlobalHotKeyGesture.MacOSDefault;

        public string ModifierDisplayNames => "Command、Option、Control 或 Shift";

        public bool Unregistered { get; private set; }

        public bool FailNextRegistration { get; init; }

        public List<GlobalHotKeyGesture> RegistrationAttempts { get; } = [];

        public GlobalHotKeyGestureCreationResult CreateGesture(
            GlobalHotKeyModifiers modifiers,
            string keyName) => new(
            GlobalHotKeyGestureCreationStatus.Created,
            new GlobalHotKeyGesture(
                modifiers | GlobalHotKeyModifiers.NoRepeat,
                9,
                "Command+Shift+V"));

        public ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
            GlobalHotKeyGesture gesture,
            CancellationToken cancellationToken)
        {
            RegistrationAttempts.Add(gesture);
            if (FailNextRegistration && RegistrationAttempts.Count == 1)
            {
                return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Conflict,
                    -9878));
            }

            CurrentGesture = gesture;
            ConfiguredGesture = gesture;
            return ValueTask.FromResult(new GlobalHotKeyRegistrationResult(
                GlobalHotKeyRegistrationStatus.Registered));
        }

        public ValueTask UnregisterAsync(CancellationToken cancellationToken)
        {
            CurrentGesture = null;
            Unregistered = true;
            return ValueTask.CompletedTask;
        }

        public void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

        public bool Disposed { get; private set; }

        public void Initialize(bool recordingPaused) => LastPausedState = recordingPaused;

        public void SetRecordingPaused(bool paused) => LastPausedState = paused;

        public void RaiseShowMain() => ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseShowQuick() => ShowQuickWindowRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseTogglePause() => RecordingPauseToggleRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseShowSettings() => ShowSettingsWindowRequested?.Invoke(this, EventArgs.Empty);

        public void RaiseExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

        public void Dispose() => Disposed = true;
    }
}
