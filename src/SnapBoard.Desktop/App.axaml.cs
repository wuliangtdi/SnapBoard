using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Desktop.Views;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using AvaloniaApplication = Avalonia.Application;

namespace SnapBoard.Desktop;

public partial class App : AvaloniaApplication, IDisposable
{
    private ServiceProvider? _services;
    private WindowsDesktopLifecycleCoordinator? _windowsLifecycle;

    internal static bool EnableNativeWindowsLifecycle { get; set; } = true;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = DesktopCompositionRoot.Build();
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
                    Program.SingleInstanceCoordinator);
                _windowsLifecycle.Initialize(startupMode);
                desktop.Exit += OnDesktopExit;

                base.OnFrameworkInitializationCompleted();
                _windowsLifecycle.CompleteStartup(startupMode);
                return;
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>(),
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

        _windowsLifecycle = null;
        _services?.Dispose();
        _services = null;
        GC.SuppressFinalize(this);
    }
}
