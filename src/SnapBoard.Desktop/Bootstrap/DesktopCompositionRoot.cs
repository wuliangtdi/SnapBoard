using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Desktop.ViewModels;

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

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
