using Avalonia;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Windows.Desktop;

namespace SnapBoard.Desktop;

internal static class Program
{
    internal static WindowsSingleInstanceCoordinator? SingleInstanceCoordinator { get; private set; }

    // Avalonia 启动前不能访问 UI、第三方组件或依赖 SynchronizationContext 的代码。
    // AOT、数据库和平台服务初始化统一放在 App 创建后的组合根中。
    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            SingleInstanceCommand command = GetSingleInstanceCommand(args);
            if (!WindowsSingleInstanceCoordinator.TryAcquire(
                    "SnapBoard.Desktop",
                    command,
                    out WindowsSingleInstanceCoordinator? coordinator,
                    out bool primaryNotified))
            {
                return primaryNotified ? 0 : 2;
            }

            SingleInstanceCoordinator = coordinator;
            if (command == SingleInstanceCommand.Exit)
            {
                coordinator?.Dispose();
                SingleInstanceCoordinator = null;
                return 0;
            }

            coordinator?.StartListening();
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                DisposeSingleInstanceCoordinator();
            }
        }
    }

    // 此方法同时供 Avalonia 设计器和后续 Headless 测试使用。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    internal static DesktopStartupMode GetStartupMode(IReadOnlyList<string>? args)
    {
        if (args?.Contains("--background", StringComparer.OrdinalIgnoreCase) == true)
        {
            return DesktopStartupMode.Background;
        }

        if (args?.Contains("--quick", StringComparer.OrdinalIgnoreCase) == true)
        {
            return DesktopStartupMode.QuickWindow;
        }

        if (args?.Contains("--settings", StringComparer.OrdinalIgnoreCase) == true)
        {
            return DesktopStartupMode.SettingsWindow;
        }

        return DesktopStartupMode.MainWindow;
    }

    internal static SingleInstanceCommand GetSingleInstanceCommand(IReadOnlyList<string> args)
    {
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            return SingleInstanceCommand.Exit;
        }

        if (args.Contains("--quick", StringComparer.OrdinalIgnoreCase))
        {
            return SingleInstanceCommand.ShowQuickWindow;
        }

        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            return SingleInstanceCommand.ShowSettingsWindow;
        }

        if (args.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            // 开机启动与后台重入只确认主实例存在，不应意外弹出主窗口。
            return SingleInstanceCommand.RemainInBackground;
        }

        return SingleInstanceCommand.ActivateMainWindow;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DisposeSingleInstanceCoordinator()
    {
        SingleInstanceCoordinator?.Dispose();
        SingleInstanceCoordinator = null;
    }
}
