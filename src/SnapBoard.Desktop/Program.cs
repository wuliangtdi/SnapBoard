using Avalonia;

namespace SnapBoard.Desktop;

internal static class Program
{
    // Avalonia 启动前不能访问 UI、第三方组件或依赖 SynchronizationContext 的代码。
    // AOT、数据库和平台服务初始化统一放在 App 创建后的组合根中。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // 此方法同时供 Avalonia 设计器和后续 Headless 测试使用。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
