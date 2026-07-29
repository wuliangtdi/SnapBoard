using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using AvaloniaApplication = Avalonia.Application;

namespace SnapBoard.Desktop.HeadlessTests;

[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopLifecycleHeadlessTests
{
    [AvaloniaFact]
    public void StartupWithoutDoubleGestureRegistersOnlyPrimary()
    {
        if (!OperatingSystem.IsWindows())
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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: true);
        context.Initialize();
        context.Foreground.Result = Protected(ForegroundWindowState.Maximized);

        context.HotKey.Raise(GlobalHotKeySlot.Primary);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        context.HotKey.Raise(GlobalHotKeySlot.Double);
        Dispatcher.UIThread.RunJobs();
        Assert.False(context.Coordinator.HasQuickWindow);

        context.Coordinator.ExecuteSingleInstanceCommand(
            SingleInstanceCommand.ShowQuickWindow);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);
        int captureCount = context.Clipboard.CaptureTargetCount;

        context.MainViewModel.OpenQuickWindowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);
        Assert.Equal(captureCount, context.Clipboard.CaptureTargetCount);

        context.Coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.CloseWindows);
        Dispatcher.UIThread.RunJobs();
        context.MainViewModel.OpenQuickWindowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(context.Coordinator.HasQuickWindow);

        context.Coordinator.ExecuteSingleInstanceCommand(SingleInstanceCommand.Exit);
        context.HotKey.Raise(GlobalHotKeySlot.Primary);
        Dispatcher.UIThread.RunJobs();
        Assert.False(context.Coordinator.HasQuickWindow);
    }

    [AvaloniaFact]
    public void PrimaryAndCompleteDoubleSequenceUseTheSameSingleQuickWindowFlow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using LifecycleContext context = new(configureDoubleGesture: true);
        context.Initialize();
        context.Foreground.Result = Normal();

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

    private sealed class LifecycleContext : IDisposable
    {
        private readonly ClassicDesktopStyleApplicationLifetime _desktop = new();
        private readonly ClipboardCaptureCoordinator _capture;
        private int _disposed;

        public LifecycleContext(bool configureDoubleGesture)
        {
            AvaloniaApplication application = AvaloniaApplication.Current ??
                throw new InvalidOperationException("Avalonia application is unavailable.");
            if (configureDoubleGesture)
            {
                LocalSettings.Update(LocalSettings.Current with
                {
                    DoubleHotKey = new GlobalHotKeyGesture(
                        GlobalHotKeyModifiers.Control |
                        GlobalHotKeyModifiers.Alt |
                        GlobalHotKeyModifiers.NoRepeat,
                        0x4B,
                        "Ctrl+Alt+K"),
                });
            }

            _capture = new ClipboardCaptureCoordinator(Clipboard, Clipboard);
            Coordinator = new WindowsDesktopLifecycleCoordinator(
                application,
                _desktop,
                MainViewModel,
                Clipboard,
                Clipboard,
                HotKey,
                new FakeAutoStartService(),
                new FakePlacementService(),
                _capture,
                singleInstance: null,
                foregroundWindowStateService: Foreground,
                localSettings: LocalSettings);
        }

        public FakeClipboardPort Clipboard { get; } = new();

        public WindowsDesktopLifecycleCoordinator Coordinator { get; }

        public FakeForegroundWindowStateService Foreground { get; } = new();

        public FakeGlobalHotKeyService HotKey { get; } = new();

        public FakeDesktopLocalSettingsService LocalSettings { get; } = new();

        public MainViewModel MainViewModel { get; } = new();

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
            _desktop.Dispose();
        }
    }

    private sealed class FakeGlobalHotKeyService : IGlobalHotKeyService, ITwoSlotGlobalHotKeyService
    {
        private readonly Dictionary<GlobalHotKeySlot, GlobalHotKeyGesture?> _gestures = new()
        {
            [GlobalHotKeySlot.Primary] = null,
            [GlobalHotKeySlot.Double] = null,
        };

        public event EventHandler? Pressed;

        public event EventHandler<GlobalHotKeyTriggeredEventArgs>? Triggered;

        public GlobalHotKeyGesture? CurrentGesture => _gestures[GlobalHotKeySlot.Primary];

        public GlobalHotKeyGesture ConfiguredGesture => GlobalHotKeyGesture.WindowsDefault;

        public GlobalHotKeyGesture DefaultGesture => GlobalHotKeyGesture.WindowsDefault;

        public string ModifierDisplayNames => "Ctrl、Alt、Shift 或 Win";

        public TimeSpan DoubleTriggerInterval => TimeSpan.FromMilliseconds(400);

        public List<GlobalHotKeySlot> RegisteredSlots { get; } = [];

        public GlobalHotKeyGestureCreationResult CreateGesture(
            GlobalHotKeyModifiers modifiers,
            string keyName) => throw new NotSupportedException();

        public GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot) => _gestures[slot];

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
            return ValueTask.CompletedTask;
        }

        public void Raise(GlobalHotKeySlot slot)
        {
            Triggered?.Invoke(this, new GlobalHotKeyTriggeredEventArgs(slot));
            if (slot == GlobalHotKeySlot.Primary)
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
            DesktopLocalSettings.CreateDefaults(GlobalHotKeyGesture.WindowsDefault);

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
            CancellationToken cancellationToken) => ValueTask.FromResult(new ClipboardReadResult(
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

    private sealed class FakeAutoStartService : IAutoStartService
    {
        public AutoStartAvailability Availability => AutoStartAvailability.Available;

        public bool IsEnabled() => false;

        public AutoStartUpdateResult SetEnabled(bool enabled) =>
            new(AutoStartUpdateStatus.Updated);
    }

    private sealed class FakePlacementService : IPlatformWindowPlacementService
    {
        public PlatformScreenPlacement? CaptureForegroundScreen() =>
            new(0, 0, 1920, 1080, 96);

        public bool CenterWindow(
            nint windowHandle,
            PlatformScreenPlacement screen,
            int widthInDeviceIndependentPixels,
            int heightInDeviceIndependentPixels) => true;

        public bool TryRestore(nint windowHandle, string placementKey) => true;

        public void Save(nint windowHandle, string placementKey) { }

        public bool TryActivate(nint windowHandle) => true;
    }
}
