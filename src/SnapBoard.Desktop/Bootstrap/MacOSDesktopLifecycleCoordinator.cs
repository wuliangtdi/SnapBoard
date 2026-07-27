using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Desktop.Views;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Desktop;

namespace SnapBoard.Desktop.Bootstrap;

/// <summary>
/// macOS 进程由原生状态栏常驻，关闭最后一个窗口不会退出。所有窗口关闭后立即解绑视觉树，
/// Dock reopen、Carbon 快捷键、状态栏和第二实例命令统一回到 Avalonia 主线程创建新窗口。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSDesktopLifecycleCoordinator : IDisposable
{
    private const string MainWindowPlacementKey = "MainWindowPlacementV1";
    private const int QuickWindowWidth = 680;
    private const int QuickWindowHeight = 480;

    private readonly IAccessibilityPermissionService _accessibilityPermissionService;
    private readonly IAutoStartService _autoStartService;
    private readonly IAutomaticPasteService _automaticPasteService;
    private readonly ClipboardCaptureCoordinator _captureCoordinator;
    private readonly IDesktopApplicationLifetime _desktop;
    private readonly IGlobalHotKeyService _hotKeyService;
    private readonly MainViewModel _mainViewModel;
    private readonly IDesktopMenuBarService _menuBarService;
    private readonly IPlatformWindowPlacementService _placementService;
    private readonly MacOSSingleInstanceCoordinator? _singleInstance;
    private readonly IClipboardWriter _writer;
    private MainWindow? _mainWindow;
    private QuickWindow? _quickWindow;
    private SettingsWindow? _settingsWindow;
    private IAutomaticPasteTarget? _foregroundTarget;
    private PlatformScreenPlacement? _foregroundScreen;
    private CancellationTokenSource? _resourceReleaseCancellation;
    private bool _restoreTargetWhenQuickCloses;
    private bool _isExiting;
    private int _disposed;

    public MacOSDesktopLifecycleCoordinator(
        IDesktopApplicationLifetime desktop,
        MainViewModel mainViewModel,
        IClipboardWriter writer,
        IAutomaticPasteService automaticPasteService,
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService,
        IAccessibilityPermissionService accessibilityPermissionService,
        IPlatformWindowPlacementService placementService,
        IDesktopMenuBarService menuBarService,
        ClipboardCaptureCoordinator captureCoordinator,
        MacOSSingleInstanceCoordinator? singleInstance)
    {
        _desktop = desktop;
        _mainViewModel = mainViewModel;
        _writer = writer;
        _automaticPasteService = automaticPasteService;
        _hotKeyService = hotKeyService;
        _autoStartService = autoStartService;
        _accessibilityPermissionService = accessibilityPermissionService;
        _placementService = placementService;
        _menuBarService = menuBarService;
        _captureCoordinator = captureCoordinator;
        _singleInstance = singleInstance;
    }

    internal bool HasMainWindow => _mainWindow is not null;

    internal bool HasQuickWindow => _quickWindow is not null;

    internal bool HasSettingsWindow => _settingsWindow is not null;

    internal SettingsViewModel? CurrentSettingsViewModel =>
        _settingsWindow?.DataContext as SettingsViewModel;

    public void Initialize(DesktopStartupMode startupMode)
    {
        _desktop.UseExplicitShutdown();
        SubscribeEvents();
        try
        {
            _menuBarService.Initialize(recordingPaused: false);
        }
        catch (Exception exception)
        {
            _mainViewModel.StatusMessage = $"菜单栏初始化失败：{exception.Message}";
        }

        GlobalHotKeyGesture configuredHotKey = _hotKeyService.ConfiguredGesture;
        GlobalHotKeyRegistrationResult hotKeyResult = RegisterHotKey(configuredHotKey);
        bool restoredDefaultHotKey = false;
        if (hotKeyResult.Status != GlobalHotKeyRegistrationStatus.Registered &&
            configuredHotKey != _hotKeyService.DefaultGesture)
        {
            GlobalHotKeyRegistrationResult fallbackResult =
                RegisterHotKey(_hotKeyService.DefaultGesture);
            restoredDefaultHotKey =
                fallbackResult.Status == GlobalHotKeyRegistrationStatus.Registered;
        }

        if (hotKeyResult.Status != GlobalHotKeyRegistrationStatus.Registered)
        {
            _mainViewModel.StatusMessage = (hotKeyResult.Status, restoredDefaultHotKey) switch
            {
                (GlobalHotKeyRegistrationStatus.Conflict, true) =>
                    "全局快捷键已被占用，已恢复默认快捷键",
                (_, true) => "全局快捷键注册失败，已恢复默认快捷键",
                (GlobalHotKeyRegistrationStatus.Conflict, false) =>
                    "全局快捷键已被占用，可在设置中更换",
                _ => "全局快捷键注册失败",
            };
        }

        _captureCoordinator.Start();
        if (startupMode == DesktopStartupMode.MainWindow)
        {
            _desktop.MainWindow = CreateMainWindow();
        }
    }

    public void CompleteStartup(DesktopStartupMode startupMode)
    {
        switch (startupMode)
        {
            case DesktopStartupMode.QuickWindow:
                ShowQuickWindow();
                break;
            case DesktopStartupMode.SettingsWindow:
                ShowSettingsWindow();
                break;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _isExiting = true;
        _restoreTargetWhenQuickCloses = false;
        CancelScheduledResourceRelease();
        UnsubscribeEvents();
        try
        {
            _hotKeyService.UnregisterAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (ObjectDisposedException)
        {
        }

        _menuBarService.Dispose();
        CloseAllWindows();
        _desktop.Dispose();
    }

    internal void ExecuteSingleInstanceCommand(SingleInstanceCommand command)
    {
        switch (command)
        {
            case SingleInstanceCommand.ActivateMainWindow:
                ShowMainWindow();
                break;
            case SingleInstanceCommand.ShowQuickWindow:
                ShowQuickWindow();
                break;
            case SingleInstanceCommand.ShowSettingsWindow:
                ShowSettingsWindow();
                break;
            case SingleInstanceCommand.Exit:
                ExitApplication();
                break;
            case SingleInstanceCommand.RemainInBackground:
                break;
            case SingleInstanceCommand.CloseWindows:
                CloseAllWindows();
                break;
        }
    }

    private void SubscribeEvents()
    {
        _mainViewModel.CopyRequested += OnCopyRequested;
        _mainViewModel.PasteRequested += OnPasteRequested;
        _mainViewModel.QuickWindowRequested += OnQuickWindowRequested;
        _mainViewModel.SettingsRequested += OnSettingsRequested;
        _mainViewModel.RecordingPauseToggleRequested += OnRecordingPauseToggleRequested;
        _mainViewModel.ExitRequested += OnExitRequested;
        _captureCoordinator.StateChanged += OnCaptureStateChanged;
        _hotKeyService.Pressed += OnHotKeyPressed;
        _menuBarService.ShowMainWindowRequested += OnShowMainWindowRequested;
        _menuBarService.ShowQuickWindowRequested += OnQuickWindowRequested;
        _menuBarService.RecordingPauseToggleRequested += OnRecordingPauseToggleRequested;
        _menuBarService.ShowSettingsWindowRequested += OnSettingsRequested;
        _menuBarService.ExitRequested += OnExitRequested;
        if (_singleInstance is not null)
        {
            _singleInstance.CommandReceived += OnSingleInstanceCommand;
        }

        _desktop.ReopenRequested += OnApplicationReopenRequested;
    }

    private GlobalHotKeyRegistrationResult RegisterHotKey(GlobalHotKeyGesture gesture) =>
        _hotKeyService.RegisterAsync(gesture, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    private void UnsubscribeEvents()
    {
        _mainViewModel.CopyRequested -= OnCopyRequested;
        _mainViewModel.PasteRequested -= OnPasteRequested;
        _mainViewModel.QuickWindowRequested -= OnQuickWindowRequested;
        _mainViewModel.SettingsRequested -= OnSettingsRequested;
        _mainViewModel.RecordingPauseToggleRequested -= OnRecordingPauseToggleRequested;
        _mainViewModel.ExitRequested -= OnExitRequested;
        _captureCoordinator.StateChanged -= OnCaptureStateChanged;
        _hotKeyService.Pressed -= OnHotKeyPressed;
        _menuBarService.ShowMainWindowRequested -= OnShowMainWindowRequested;
        _menuBarService.ShowQuickWindowRequested -= OnQuickWindowRequested;
        _menuBarService.RecordingPauseToggleRequested -= OnRecordingPauseToggleRequested;
        _menuBarService.ShowSettingsWindowRequested -= OnSettingsRequested;
        _menuBarService.ExitRequested -= OnExitRequested;
        if (_singleInstance is not null)
        {
            _singleInstance.CommandReceived -= OnSingleInstanceCommand;
        }

        _desktop.ReopenRequested -= OnApplicationReopenRequested;
    }

    private MainWindow CreateMainWindow()
    {
        CancelScheduledResourceRelease();
        MainWindow window = new()
        {
            DataContext = _mainViewModel,
        };
        window.Opened += OnMainWindowOpened;
        window.Closing += OnMainWindowClosing;
        window.Closed += OnMainWindowClosed;
        _mainWindow = window;
        return window;
    }

    private void ShowMainWindow()
    {
        CaptureForegroundContext();
        MainWindow window = _mainWindow ?? CreateMainWindow();
        _desktop.MainWindow = window;
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        ActivateNativeWindow(window);
    }

    private void ShowQuickWindow()
    {
        CancelScheduledResourceRelease();
        if (_quickWindow is not null)
        {
            _quickWindow.Activate();
            ActivateNativeWindow(_quickWindow);
            return;
        }

        // 必须在 NSWindow 创建和应用激活之前保存目标应用与屏幕；否则会错误捕获 SnapBoard 自身。
        CaptureForegroundContext(force: true);
        _restoreTargetWhenQuickCloses = true;
        QuickWindow window = new()
        {
            DataContext = _mainViewModel,
        };
        window.Opened += OnQuickWindowOpened;
        window.Closed += OnQuickWindowClosed;
        window.DismissRequested += OnQuickWindowDismissRequested;
        window.PasteRequested += OnQuickWindowPasteRequested;
        _quickWindow = window;
        if (_desktop.MainWindow is null)
        {
            _desktop.MainWindow = window;
        }

        window.Show();
        window.Activate();
    }

    private void ShowSettingsWindow()
    {
        CancelScheduledResourceRelease();
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            ActivateNativeWindow(_settingsWindow);
            return;
        }

        SettingsWindow window = new()
        {
            Height = 650,
            DataContext = new SettingsViewModel(
                _hotKeyService,
                _autoStartService,
                _accessibilityPermissionService),
        };
        window.Closed += OnSettingsWindowClosed;
        _settingsWindow = window;
        if (_desktop.MainWindow is null)
        {
            _desktop.MainWindow = window;
        }

        if (_mainWindow is { IsVisible: true } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }

        window.Activate();
    }

    private void CaptureForegroundContext(bool force = false)
    {
        if (!force && (_mainWindow is { IsActive: true } || _quickWindow is { IsActive: true }))
        {
            return;
        }

        _foregroundTarget = _automaticPasteService.CaptureForegroundTarget();
        _foregroundScreen = _placementService.CaptureForegroundScreen();
    }

    private async Task CopySelectedAsync(bool plainText)
    {
        ClipboardHistoryItemViewModel? item = _mainViewModel.SelectedItem;
        if (item is null)
        {
            return;
        }

        ClipboardWriteResult result = plainText
            ? await _writer.WritePlainTextAsync(item.Content, CancellationToken.None)
            : await _writer.WriteAsync(
                new ClipboardWriteRequest { Text = item.Content },
                CancellationToken.None);
        _mainViewModel.StatusMessage = result.Status is ClipboardWriteStatus.Success or ClipboardWriteStatus.Partial
            ? "已复制到剪贴板"
            : "写入剪贴板失败";
    }

    private async Task PasteSelectedAsync(bool plainText)
    {
        ClipboardHistoryItemViewModel? item = _mainViewModel.SelectedItem;
        if (item is null)
        {
            return;
        }

        IAutomaticPasteTarget? target = _foregroundTarget;
        ClipboardWriteResult writeResult = plainText
            ? await _writer.WritePlainTextAsync(item.Content, CancellationToken.None)
            : await _writer.WriteAsync(
                new ClipboardWriteRequest { Text = item.Content },
                CancellationToken.None);
        if (writeResult.Status is not (ClipboardWriteStatus.Success or ClipboardWriteStatus.Partial))
        {
            _mainViewModel.StatusMessage = "写入剪贴板失败";
            return;
        }

        _restoreTargetWhenQuickCloses = false;
        CloseQuickWindow();
        if (target is null)
        {
            _mainViewModel.StatusMessage = AutomaticPasteResult.ManualPasteRequiredMessage;
            return;
        }

        AutomaticPasteResult pasteResult =
            await _automaticPasteService.TryPasteAsync(target, CancellationToken.None);
        _mainViewModel.StatusMessage = pasteResult.Status == AutomaticPasteStatus.Pasted
            ? "已粘贴"
            : AutomaticPasteResult.ManualPasteRequiredMessage;
    }

    private async Task RestoreForegroundTargetAsync()
    {
        IAutomaticPasteTarget? target = _foregroundTarget;
        _restoreTargetWhenQuickCloses = false;
        CloseQuickWindow();
        await TryRestoreTargetAsync(target);
    }

    private async Task TryRestoreTargetAsync(IAutomaticPasteTarget? target)
    {
        if (target is null || _isExiting)
        {
            return;
        }

        ForegroundActivationResult result =
            await _automaticPasteService.TryActivateTargetAsync(target, CancellationToken.None);
        if (result.Status != ForegroundActivationStatus.Activated)
        {
            _mainViewModel.StatusMessage = "原应用已不可用";
        }
    }

    private void ToggleRecordingPause()
    {
        bool paused = !_captureCoordinator.IsPaused;
        _captureCoordinator.SetPaused(paused);
        _mainViewModel.UpdateRecordingState(paused);
        _menuBarService.SetRecordingPaused(paused);
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _restoreTargetWhenQuickCloses = false;
        CloseAllWindows();
        _desktop.TryShutdown();
    }

    private void CloseAllWindows()
    {
        _quickWindow?.Close();
        _settingsWindow?.Close();
        _mainWindow?.Close();
    }

    private void CloseQuickWindow() => _quickWindow?.Close();

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            nint handle = GetWindowHandle(window);
            if (handle != 0)
            {
                _placementService.TryRestore(handle, MainWindowPlacementKey);
            }
        }
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is MainWindow window)
        {
            nint handle = GetWindowHandle(window);
            if (handle != 0)
            {
                _placementService.Save(handle, MainWindowPlacementKey);
            }
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.DataContext = null;
            window.Opened -= OnMainWindowOpened;
            window.Closing -= OnMainWindowClosing;
            window.Closed -= OnMainWindowClosed;
        }

        _mainWindow = null;
        if (!_isExiting)
        {
            _desktop.MainWindow = null;
            ScheduleBackgroundResourceReleaseIfIdle();
        }
    }

    private void OnQuickWindowOpened(object? sender, EventArgs e)
    {
        if (sender is QuickWindow window)
        {
            nint handle = GetWindowHandle(window);
            if (handle != 0 && _foregroundScreen is PlatformScreenPlacement screen)
            {
                _placementService.CenterWindow(
                    handle,
                    screen,
                    QuickWindowWidth,
                    QuickWindowHeight);
            }

            ActivateNativeWindow(window);
        }
    }

    private void OnQuickWindowClosed(object? sender, EventArgs e)
    {
        IAutomaticPasteTarget? target = _foregroundTarget;
        bool shouldRestore = _restoreTargetWhenQuickCloses;
        _restoreTargetWhenQuickCloses = false;
        _foregroundTarget = null;
        _foregroundScreen = null;
        if (sender is QuickWindow window)
        {
            window.DataContext = null;
            window.Opened -= OnQuickWindowOpened;
            window.Closed -= OnQuickWindowClosed;
            window.DismissRequested -= OnQuickWindowDismissRequested;
            window.PasteRequested -= OnQuickWindowPasteRequested;
        }

        _quickWindow = null;
        if (ReferenceEquals(_desktop.MainWindow, sender) && !_isExiting)
        {
            _desktop.MainWindow = null;
        }

        ScheduleBackgroundResourceReleaseIfIdle();
        if (shouldRestore)
        {
            _ = TryRestoreTargetAsync(target);
        }
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow window)
        {
            window.DataContext = null;
            window.Closed -= OnSettingsWindowClosed;
        }

        _settingsWindow = null;
        if (ReferenceEquals(_desktop.MainWindow, sender) && !_isExiting)
        {
            _desktop.MainWindow = null;
        }

        ScheduleBackgroundResourceReleaseIfIdle();
    }

    private async void OnCopyRequested(object? sender, EventArgs e) =>
        await CopySelectedAsync(plainText: false);

    private async void OnPasteRequested(object? sender, EventArgs e) =>
        await PasteSelectedAsync(plainText: false);

    private void OnShowMainWindowRequested(object? sender, EventArgs e) =>
        PostToUi(ShowMainWindow);

    private void OnQuickWindowRequested(object? sender, EventArgs e) =>
        PostToUi(ShowQuickWindow);

    private void OnSettingsRequested(object? sender, EventArgs e) =>
        PostToUi(ShowSettingsWindow);

    private void OnRecordingPauseToggleRequested(object? sender, EventArgs e) =>
        PostToUi(ToggleRecordingPause);

    private void OnExitRequested(object? sender, EventArgs e) => PostToUi(ExitApplication);

    private void OnHotKeyPressed(object? sender, EventArgs e) => PostToUi(() =>
    {
        if (_settingsWindow is not { IsActive: true })
        {
            ShowQuickWindow();
        }
    });

    private void OnSingleInstanceCommand(SingleInstanceCommand command) =>
        PostToUi(() => ExecuteSingleInstanceCommand(command));

    private void OnApplicationReopenRequested(object? sender, EventArgs e) =>
        PostToUi(ShowMainWindow);

    private void OnCaptureStateChanged(ClipboardCaptureState state) => PostToUi(() =>
    {
        if (state.ErrorMessage is not null)
        {
            _mainViewModel.StatusMessage = "剪贴板监听已停止";
        }

        _mainViewModel.IsRecordingPaused = state.IsPaused;
        _menuBarService.SetRecordingPaused(state.IsPaused);
    });

    private async void OnQuickWindowDismissRequested(object? sender, EventArgs e) =>
        await RestoreForegroundTargetAsync();

    private async void OnQuickWindowPasteRequested(
        object? sender,
        QuickPasteRequestedEventArgs e) =>
        await PasteSelectedAsync(e.PlainText);

    private void PostToUi(Action action)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Dispatcher.UIThread.Post(action, DispatcherPriority.Input);
        }
    }

    private void ActivateNativeWindow(Window window)
    {
        nint handle = GetWindowHandle(window);
        if (handle != 0)
        {
            _placementService.TryActivate(handle);
        }
    }

    private void ScheduleBackgroundResourceReleaseIfIdle()
    {
        if (_isExiting || _mainWindow is not null || _quickWindow is not null || _settingsWindow is not null)
        {
            return;
        }

        CancelScheduledResourceRelease();
        CancellationTokenSource cancellation = new();
        _resourceReleaseCancellation = cancellation;
        _ = Task.Run(
            () => ReleaseClosedWindowResourcesAsync(cancellation),
            CancellationToken.None);
    }

    private async Task ReleaseClosedWindowResourcesAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellation.Token).ConfigureAwait(false);

            // 状态栏常驻时进程可能长期不再分配；最后窗口关闭后只做一次完整回收，
            // 释放 Avalonia 视觉树和 Skia 终结器，不在 AppKit 或 UI 回调中执行阻塞 GC。
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _resourceReleaseCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private void CancelScheduledResourceRelease()
    {
        CancellationTokenSource? cancellation =
            Interlocked.Exchange(ref _resourceReleaseCancellation, null);
        cancellation?.Cancel();
    }

    private static nint GetWindowHandle(Window window) =>
        window.TryGetPlatformHandle()?.Handle ?? 0;
}
