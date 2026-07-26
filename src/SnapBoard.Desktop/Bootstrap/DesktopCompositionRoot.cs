using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS;
using SnapBoard.Platform.Windows;

namespace SnapBoard.Desktop.Bootstrap;

/// <summary>
/// 桌面进程唯一的依赖组合根。所有平台实现、基础设施和用例都应在这里显式注册，
/// 禁止使用程序集扫描，以免破坏 Native AOT 的可裁剪性和启动性能。
/// </summary>
internal static class DesktopCompositionRoot
{
    public static ServiceProvider Build()
    {
        ServiceCollection services = new();
        services.AddSingleton<MainViewModel>();

        if (OperatingSystem.IsWindows())
        {
            AddWindowsClipboardServices(services);
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddMacOSClipboardServices(services);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsClipboardServices(IServiceCollection services)
    {
        services.AddSingleton<WindowsClipboardAdapter>();
        services.AddSingleton<IClipboardMonitor>(provider =>
            provider.GetRequiredService<WindowsClipboardAdapter>());
        services.AddSingleton<IClipboardContentReader>(provider =>
            provider.GetRequiredService<WindowsClipboardAdapter>());
        services.AddSingleton<IClipboardWriter>(provider =>
            provider.GetRequiredService<WindowsClipboardAdapter>());
        services.AddSingleton<IAutomaticPasteService>(provider =>
            provider.GetRequiredService<WindowsClipboardAdapter>());
    }

    [SupportedOSPlatform("macos")]
    private static void AddMacOSClipboardServices(IServiceCollection services)
    {
        services.AddSingleton<MacOSClipboardAdapter>();
        services.AddSingleton<IClipboardMonitor>(provider =>
            provider.GetRequiredService<MacOSClipboardAdapter>());
        services.AddSingleton<IClipboardContentReader>(provider =>
            provider.GetRequiredService<MacOSClipboardAdapter>());
        services.AddSingleton<IClipboardWriter>(provider =>
            provider.GetRequiredService<MacOSClipboardAdapter>());
        services.AddSingleton<IAutomaticPasteService>(provider =>
            provider.GetRequiredService<MacOSClipboardAdapter>());
    }
}
