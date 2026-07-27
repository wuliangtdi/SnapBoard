using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;

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
            null);

        try
        {
            coordinator.Initialize(DesktopStartupMode.Background);
            Dispatcher.UIThread.RunJobs();
            Assert.False(coordinator.HasMainWindow);

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
            Assert.True(settings.IsRestrictedMode);
            Assert.DoesNotContain("Windows", settings.HotKeyStatus, StringComparison.OrdinalIgnoreCase);

            coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.CloseWindows);
            Dispatcher.UIThread.RunJobs();
            Assert.False(coordinator.HasMainWindow);
            Assert.False(coordinator.HasQuickWindow);
            Assert.False(coordinator.HasSettingsWindow);

            menu.RaiseShowMain();
            menu.RaiseShowQuick();
            menu.RaiseShowSettings();
            Dispatcher.UIThread.RunJobs();
            Assert.True(coordinator.HasMainWindow);
            Assert.True(coordinator.HasQuickWindow);
            Assert.True(coordinator.HasSettingsWindow);

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
