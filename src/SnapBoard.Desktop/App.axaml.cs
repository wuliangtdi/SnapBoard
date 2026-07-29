using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Application.Sync;
using SnapBoard.Application.Updates;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Desktop.Views;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Abstractions.Storage;
using AvaloniaApplication = Avalonia.Application;

namespace SnapBoard.Desktop;

public partial class App : AvaloniaApplication, IDisposable
{
    private ServiceProvider? _services;
    private MacOSDesktopLifecycleCoordinator? _macOSLifecycle;
    private WindowsDesktopLifecycleCoordinator? _windowsLifecycle;

    internal static bool EnableNativeWindowsLifecycle { get; set; } = true;

    internal static bool EnableNativeMacOSLifecycle { get; set; } = true;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = DesktopCompositionRoot.Build(Program.StorageStartup);
            if (OperatingSystem.IsWindows() && EnableNativeWindowsLifecycle)
            {
                DesktopStartupMode startupMode = Program.GetStartupMode(desktop.Args);
                _windowsLifecycle = new WindowsDesktopLifecycleCoordinator(
                    this,
                    desktop,
                    _services.GetRequiredService<MainViewModel>(),
                    _services.GetRequiredService<IClipboardWriter>(),
                    _services.GetRequiredService<IAutomaticPasteService>(),
                    _services.GetRequiredService<IGlobalHotKeyService>(),
                    _services.GetRequiredService<IAutoStartService>(),
                    _services.GetRequiredService<IPlatformWindowPlacementService>(),
                    _services.GetRequiredService<ClipboardCaptureCoordinator>(),
                    Program.SingleInstanceCoordinator,
                    _services.GetService<IStorageManagementService>(),
                    _services.GetService<IStorageMigrationBarrier>(),
                    _services.GetService<IStoragePlatformService>(),
                    _services.GetService<ISyncService>(),
                    _services.GetService<IHistorySettingsService>(),
                    _services.GetRequiredService<IApplicationUpdateService>(),
                    _services.GetRequiredService<IPlatformForegroundWindowStateService>(),
                    _services.GetRequiredService<IDesktopLocalSettingsService>());
                _windowsLifecycle.Initialize(startupMode);
                desktop.Exit += OnDesktopExit;

                base.OnFrameworkInitializationCompleted();
                _windowsLifecycle.CompleteStartup(startupMode);
                _services.GetService<StorageStartupAcknowledgementCoordinator>()?.Start();
                return;
            }

            if (OperatingSystem.IsMacOS() && EnableNativeMacOSLifecycle)
            {
                bool launchedAsLoginItem = _services
                    .GetRequiredService<ILaunchContextService>()
                    .WasLaunchedAsLoginItem();
                DesktopStartupMode startupMode = Program.GetStartupMode(
                    desktop.Args,
                    launchedAsLoginItem);
                _macOSLifecycle = new MacOSDesktopLifecycleCoordinator(
                    new AvaloniaDesktopApplicationLifetime(desktop),
                    _services.GetRequiredService<MainViewModel>(),
                    _services.GetRequiredService<IClipboardWriter>(),
                    _services.GetRequiredService<IAutomaticPasteService>(),
                    _services.GetRequiredService<IGlobalHotKeyService>(),
                    _services.GetRequiredService<IAutoStartService>(),
                    _services.GetRequiredService<IAccessibilityPermissionService>(),
                    _services.GetRequiredService<IPlatformWindowPlacementService>(),
                    _services.GetRequiredService<IDesktopMenuBarService>(),
                    _services.GetRequiredService<ClipboardCaptureCoordinator>(),
                    Program.MacSingleInstanceCoordinator,
                    _services.GetService<IStorageManagementService>(),
                    _services.GetService<IStorageMigrationBarrier>(),
                    _services.GetService<IStoragePlatformService>(),
                    _services.GetService<ISyncService>(),
                    _services.GetService<IHistorySettingsService>(),
                    _services.GetService<IDesktopSystemEventService>(),
                    _services.GetRequiredService<IApplicationUpdateService>());
                _macOSLifecycle.Initialize(startupMode);
                desktop.Exit += OnDesktopExit;

                base.OnFrameworkInitializationCompleted();
                _macOSLifecycle.CompleteStartup(startupMode);
                _services.GetService<StorageStartupAcknowledgementCoordinator>()?.Start();
                return;
            }

            MainViewModel mainViewModel = _services.GetRequiredService<MainViewModel>();
            mainViewModel.Start();
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel,
            };
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
        => Dispose();

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _windowsLifecycle?.Dispose();
        }
        else if (OperatingSystem.IsMacOS())
        {
            _macOSLifecycle?.Dispose();
        }

        _macOSLifecycle = null;
        _windowsLifecycle = null;
        _services?.Dispose();
        _services = null;
        GC.SuppressFinalize(this);
    }
}
