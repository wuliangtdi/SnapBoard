using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Desktop.Views;
using AvaloniaApplication = Avalonia.Application;

namespace SnapBoard.Desktop;

public partial class App : AvaloniaApplication
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = DesktopCompositionRoot.Build();
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>(),
            };
            desktop.Exit += (_, _) => _services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
