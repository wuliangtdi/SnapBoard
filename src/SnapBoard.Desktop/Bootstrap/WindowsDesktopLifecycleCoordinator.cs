using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Desktop.Views;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Desktop;
using AvaloniaApplication = Avalonia.Application;

namespace SnapBoard.Desktop.Bootstrap;

internal enum DesktopStartupMode
{
    MainWindow = 0,
    Background = 1,
    QuickWindow = 2,
    SettingsWindow = 3,
}

/// <summary>
/// Avalonia 只负责窗口、托盘和 Dispatcher 生命周期；所有原生句柄操作均通过平台端口。
/// 主窗口、快速窗口和设置窗口关闭后立即解除引用，让托盘常驻态可以回收完整视觉树。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDesktopLifecycleCoordinator : IDisposable
{
    private const string MainWindowPlacementKey = "MainWindowPlacementV1";
    private const int QuickWindowWidth = 680;
    private const int QuickWindowHeight = 480;

    private readonly AvaloniaApplication _application;
    private readonly IAutoStartService _autoStartService;
    private readonly IAutomaticPasteService _automaticPasteService;
    private readonly ClipboardCaptureCoordinator _captureCoordinator;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly IGlobalHotKeyService _hotKeyService;
    private readonly MainViewModel _mainViewModel;
    private readonly IPlatformWindowPlacementService _placementService;
    private readonly WindowsSingleInstanceCoordinator? _singleInstance;
    private readonly IClipboardWriter _writer;
    private MainWindow? _mainWindow;
    private QuickWindow? _quickWindow;
    private SettingsWindow? _settingsWindow;
    private TrayIcon? _trayIcon;
    private TrayIcons? _trayIcons;
    private NativeMenuItem? _pauseMenuItem;
    private IAutomaticPasteTarget? _foregroundTarget;
    private PlatformScreenPlacement? _foregroundScreen;
    private CancellationTokenSource? _resourceReleaseCancellation;
    private bool _isExiting;
    private int _disposed;

    public WindowsDesktopLifecycleCoordinator(
        AvaloniaApplication application,
        IClassicDesktopStyleApplicationLifetime desktop,
        MainViewModel mainViewModel,
        IClipboardWriter writer,
        IAutomaticPasteService automaticPasteService,
        IGlobalHotKeyService hotKeyService,
        IAutoStartService autoStartService,
        IPlatformWindowPlacementService placementService,
        ClipboardCaptureCoordinator captureCoordinator,
        WindowsSingleInstanceCoordinator? singleInstance)
    {
        _application = application;
        _desktop = desktop;
        _mainViewModel = mainViewModel;
        _writer = writer;
        _automaticPasteService = automaticPasteService;
        _hotKeyService = hotKeyService;
        _autoStartService = autoStartService;
        _placementService = placementService;
        _captureCoordinator = captureCoordinator;
        _singleInstance = singleInstance;
    }

    public void Initialize(DesktopStartupMode startupMode)
    {
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SubscribeEvents();
        CreateTrayIcon();

        GlobalHotKeyRegistrationResult hotKeyResult = _hotKeyService.RegisterAsync(
                _hotKeyService.ConfiguredGesture,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (hotKeyResult.Status != GlobalHotKeyRegistrationStatus.Registered)
        {
            _mainViewModel.StatusMessage = hotKeyResult.Status == GlobalHotKeyRegistrationStatus.Conflict
                ? "全局快捷键已被占用，可在设置中更换"
                : "全局快捷键注册失败";
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
        CancelScheduledResourceRelease();
        UnsubscribeEvents();
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_trayIcons is not null)
        {
            TrayIcon.SetIcons(_application, new TrayIcons());
            _trayIcons = null;
        }

        CloseAllWindows();
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
        if (_singleInstance is not null)
        {
            _singleInstance.CommandReceived += OnSingleInstanceCommand;
        }
    }

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
        if (_singleInstance is not null)
        {
            _singleInstance.CommandReceived -= OnSingleInstanceCommand;
        }
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

        window.WindowState = WindowState.Normal;
        window.Activate();
        nint handle = GetWindowHandle(window);
        if (handle != 0)
        {
            _placementService.TryActivate(handle);
        }
    }

    private void ShowQuickWindow()
    {
        CancelScheduledResourceRelease();
        if (_quickWindow is not null)
        {
            _quickWindow.Activate();
            return;
        }

        CaptureForegroundContext();
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
    }

    private void ShowSettingsWindow()
    {
        CancelScheduledResourceRelease();
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        SettingsWindow window = new()
        {
            DataContext = new SettingsViewModel(_hotKeyService, _autoStartService),
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
    }

    private void CreateTrayIcon()
    {
        try
        {
            NativeMenu menu = new();
            NativeMenuItem showMain = new("打开 SnapBoard");
            showMain.Click += (_, _) => PostToUi(ShowMainWindow);
            NativeMenuItem showQuick = new("快速粘贴");
            showQuick.Click += (_, _) => PostToUi(ShowQuickWindow);
            NativeMenuItem settings = new("设置");
            settings.Click += (_, _) => PostToUi(ShowSettingsWindow);
            _pauseMenuItem = new NativeMenuItem("暂停记录")
            {
                ToggleType = MenuItemToggleType.CheckBox,
            };
            _pauseMenuItem.Click += (_, _) => PostToUi(ToggleRecordingPause);
            NativeMenuItem exit = new("退出");
            exit.Click += (_, _) => PostToUi(ExitApplication);

            menu.Items.Add(showMain);
            menu.Items.Add(showQuick);
            menu.Items.Add(_pauseMenuItem);
            menu.Items.Add(settings);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);

            using Stream iconStream = AssetLoader.Open(
                new Uri("avares://SnapBoard.Desktop/Assets/snapboard-logo.png"));
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                IsVisible = true,
                Menu = menu,
                ToolTipText = "SnapBoard - 闪剪",
            };
            _trayIcon.Clicked += (_, _) => PostToUi(ShowMainWindow);
            _trayIcons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(_application, _trayIcons);
        }
        catch (Exception exception)
        {
            _mainViewModel.StatusMessage = $"系统托盘初始化失败：{exception.Message}";
            _trayIcon?.Dispose();
            _trayIcon = null;
            _trayIcons = null;
        }
    }

    private void CaptureForegroundContext()
    {
        if (_mainWindow is { IsActive: true } || _quickWindow is { IsActive: true })
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
        CloseQuickWindow();
        if (target is null)
        {
            return;
        }

        ForegroundActivationResult result =
            await _automaticPasteService.TryActivateTargetAsync(target, CancellationToken.None);
        if (result.Status != ForegroundActivationStatus.Activated)
        {
            _mainViewModel.StatusMessage = "原窗口已不可用";
        }
    }

    private void ToggleRecordingPause()
    {
        bool paused = !_captureCoordinator.IsPaused;
        _captureCoordinator.SetPaused(paused);
        _mainViewModel.UpdateRecordingState(paused);
        if (_pauseMenuItem is not null)
        {
            _pauseMenuItem.IsChecked = paused;
            _pauseMenuItem.Header = paused ? "恢复记录" : "暂停记录";
        }
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        CloseAllWindows();
        _desktop.TryShutdown();
    }

    private void CloseAllWindows()
    {
        _quickWindow?.Close();
        _settingsWindow?.Close();
        _mainWindow?.Close();
    }

    private void CloseQuickWindow()
    {
        _quickWindow?.Close();
    }

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
        if (sender is QuickWindow window && _foregroundScreen is PlatformScreenPlacement screen)
        {
            nint handle = GetWindowHandle(window);
            if (handle != 0)
            {
                _placementService.CenterWindow(
                    handle,
                    screen,
                    QuickWindowWidth,
                    QuickWindowHeight);
            }
        }
    }

    private void OnQuickWindowClosed(object? sender, EventArgs e)
    {
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

    private void OnQuickWindowRequested(object? sender, EventArgs e) => ShowQuickWindow();

    private void OnSettingsRequested(object? sender, EventArgs e) => ShowSettingsWindow();

    private void OnRecordingPauseToggleRequested(object? sender, EventArgs e) =>
        ToggleRecordingPause();

    private void OnExitRequested(object? sender, EventArgs e) => ExitApplication();

    private void OnHotKeyPressed(object? sender, EventArgs e) => PostToUi(() =>
    {
        // 设置窗口激活时，用户可能正在录入当前已注册的组合键。此时只吞掉原生
        // WM_HOTKEY，不能再弹出快速窗口打断焦点和键盘捕获。
        if (_settingsWindow is not { IsActive: true })
        {
            ShowQuickWindow();
        }
    });

    private void OnSingleInstanceCommand(SingleInstanceCommand command) =>
        PostToUi(() => ExecuteSingleInstanceCommand(command));

    private void ExecuteSingleInstanceCommand(SingleInstanceCommand command)
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
        }
    }

    private void OnCaptureStateChanged(ClipboardCaptureState state) => PostToUi(() =>
    {
        if (state.ErrorMessage is not null)
        {
            _mainViewModel.StatusMessage = "剪贴板监听已停止";
        }

        _mainViewModel.IsRecordingPaused = state.IsPaused;
        if (_pauseMenuItem is not null)
        {
            _pauseMenuItem.IsChecked = state.IsPaused;
        }
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

            // 托盘常驻且没有任何窗口时，进程可能长期不再分配，GC 因而不会主动回收
            // Avalonia 视觉树和 Skia 终结器。这里每次“最后窗口关闭”只执行一次完整回收，
            // 不在 UI/消息线程运行，也不形成周期性 GC。
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
