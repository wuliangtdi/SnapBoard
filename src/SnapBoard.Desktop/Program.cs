using Avalonia;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Desktop;
using SnapBoard.Platform.Windows.Desktop;
using SnapBoard.Update.Velopack;

namespace SnapBoard.Desktop;

internal static class Program
{
    internal static WindowsSingleInstanceCoordinator? SingleInstanceCoordinator { get; private set; }

    internal static MacOSSingleInstanceCoordinator? MacSingleInstanceCoordinator { get; private set; }

    internal static DesktopStorageStartupContext? StorageStartup { get; private set; }

    // Avalonia 启动前不能访问 UI、第三方组件或依赖 SynchronizationContext 的代码。
    // AOT、数据库和平台服务初始化统一放在 App 创建后的组合根中。
    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacOSApplicationIdentity.SetProcessName();
        }

        VelopackBootstrap.Run(args);

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
            try
            {
                StorageStartup = WindowsStorageStartupContext.Create(
                    GetOptionValue(args, "--storage-bootstrap-root"),
                    GetOptionValue(args, "--migration-id"));
            }
            catch
            {
                DisposeSingleInstanceCoordinator();
                throw;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            SingleInstanceCommand command = GetSingleInstanceCommand(args);
            if (!MacOSSingleInstanceCoordinator.TryAcquire(
                    "com.wuliangtdi.snapboard",
                    command,
                    out MacOSSingleInstanceCoordinator? coordinator,
                    out bool primaryNotified))
            {
                return primaryNotified ? 0 : 2;
            }

            MacSingleInstanceCoordinator = coordinator;
            if (command == SingleInstanceCommand.Exit)
            {
                coordinator?.Dispose();
                MacSingleInstanceCoordinator = null;
                return 0;
            }

            coordinator?.StartListening();
            try
            {
                StorageStartup = MacOSStorageStartupContext.Create(
                    GetOptionValue(args, "--storage-bootstrap-root"),
                    GetOptionValue(args, "--migration-id"));
            }
            catch
            {
                DisposeMacOSSingleInstanceCoordinator();
                throw;
            }
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
                DisposeStorageStartup();
            }
            else if (OperatingSystem.IsMacOS())
            {
                DisposeMacOSSingleInstanceCoordinator();
                DisposeStorageStartup();
            }
        }
    }

    // 此方法同时供 Avalonia 设计器和后续 Headless 测试使用。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions
            {
                DisableSetProcessName = true,
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    internal static DesktopStartupMode GetStartupMode(
        IReadOnlyList<string>? args,
        bool launchedAsLoginItem = false)
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

        if (launchedAsLoginItem)
        {
            return DesktopStartupMode.Background;
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

        if (args.Contains("--close-windows", StringComparer.OrdinalIgnoreCase))
        {
            return SingleInstanceCommand.CloseWindows;
        }

        return SingleInstanceCommand.ActivateMainWindow;
    }

    internal static string? GetOptionValue(
        IReadOnlyList<string> args,
        string optionName)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionName);
        string? value = null;
        for (int index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value is not null || index + 1 >= args.Count ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid or duplicate option: {optionName}.", nameof(args));
            }

            value = args[++index];
        }

        return value;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DisposeSingleInstanceCoordinator()
    {
        SingleInstanceCoordinator?.Dispose();
        SingleInstanceCoordinator = null;
    }

    private static void DisposeStorageStartup()
    {
        StorageStartup?.Dispose();
        StorageStartup = null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void DisposeMacOSSingleInstanceCoordinator()
    {
        MacSingleInstanceCoordinator?.Dispose();
        MacSingleInstanceCoordinator = null;
    }
}
