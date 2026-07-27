using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Application.Clipboard;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Infrastructure.Persistence;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Abstractions.Security;
using SnapBoard.Platform.MacOS;
using SnapBoard.Platform.MacOS.Desktop;
using SnapBoard.Platform.MacOS.Security;
using SnapBoard.Platform.Windows;
using SnapBoard.Platform.Windows.Desktop;
using SnapBoard.Platform.Windows.Security;

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
        AddClipboardHistoryServices(services);
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

    private static void AddClipboardHistoryServices(IServiceCollection services)
    {
        services.AddSingleton(SnapBoardStoragePaths.CreateDefault());
        services.AddSingleton(provider => new SnapBoardDatabaseConnectionFactory(
            provider.GetRequiredService<SnapBoardStoragePaths>().DatabasePath));
        services.AddSingleton<SnapBoardDatabaseMigrator>();
        services.AddSingleton<SqliteClipboardHistoryStore>();
        services.AddSingleton<IClipboardHistoryStore>(provider =>
            provider.GetRequiredService<SqliteClipboardHistoryStore>());
        services.AddSingleton<ClipboardHistoryChangeNotifier>();
        services.AddSingleton<IClipboardHistoryService, ClipboardHistoryService>();

        services.AddSingleton(new ClipboardCaptureOptions());
        services.AddSingleton(ClipboardRetentionPolicy.Default);
        // 责任链顺序是安全边界：先阻断自身反馈和敏感来源，再执行应用规则与容量判断。
        services.AddSingleton<IClipboardCapturePolicy, CurrentApplicationClipboardPolicy>();
        services.AddSingleton<IClipboardCapturePolicy, SensitiveClipboardPolicy>();
        services.AddSingleton<IClipboardCapturePolicy, ApplicationRuleClipboardPolicy>();
        services.AddSingleton<IClipboardCapturePolicy, PayloadSizeClipboardPolicy>();
        services.AddSingleton<IClipboardCapturePolicy, SupportedContentClipboardPolicy>();
        services.AddSingleton<IClipboardCapturePolicyChain, ClipboardCapturePolicyChain>();
        services.AddSingleton<IClipboardCaptureService, ClipboardCaptureService>();
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
        services.AddSingleton<IGlobalHotKeyService, WindowsGlobalHotKeyService>();
        services.AddSingleton<IAutoStartService, WindowsAutoStartService>();
        services.AddSingleton<IPlatformWindowPlacementService, WindowsWindowPlacementService>();
        services.AddSingleton<IPlatformSecretStore, WindowsCredentialSecretStore>();
        services.AddSingleton<ClipboardCaptureCoordinator>();
    }

    [SupportedOSPlatform("macos")]
    private static void AddMacOSClipboardServices(IServiceCollection services)
    {
        services.AddSingleton<IPlatformMainThreadDispatcher, AvaloniaMainThreadDispatcher>();
        services.AddSingleton<MacOSClipboardAdapter>();
        services.AddSingleton<IClipboardMonitor>(provider =>
            provider.GetRequiredService<MacOSClipboardAdapter>());
        services.AddSingleton<IClipboardContentReader>(provider =>
            provider.GetRequiredService<MacOSClipboardAdapter>());
        services.AddSingleton<IClipboardWriter>(provider =>
            provider.GetRequiredService<MacOSClipboardAdapter>());
        services.AddSingleton<IAutomaticPasteService>(provider =>
            provider.GetRequiredService<MacOSClipboardAdapter>());
        services.AddSingleton<IGlobalHotKeyService, MacOSGlobalHotKeyService>();
        services.AddSingleton<IAutoStartService, MacOSAutoStartService>();
        services.AddSingleton<IAccessibilityPermissionService, MacOSAccessibilityPermissionService>();
        services.AddSingleton<IPlatformWindowPlacementService, MacOSWindowPlacementService>();
        services.AddSingleton<IDesktopMenuBarService, MacOSMenuBarService>();
        services.AddSingleton<ILaunchContextService, MacOSLaunchContextService>();
        services.AddSingleton<IPlatformSecretStore, MacOSKeychainSecretStore>();
        services.AddSingleton<ClipboardCaptureCoordinator>();
    }
}
