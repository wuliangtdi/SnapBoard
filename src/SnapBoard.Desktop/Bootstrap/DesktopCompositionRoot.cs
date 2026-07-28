using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Application.Sync;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Infrastructure.Persistence;
using SnapBoard.Infrastructure.Sync;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Abstractions.Security;
using SnapBoard.Platform.Abstractions.Storage;
using SnapBoard.Platform.MacOS;
using SnapBoard.Platform.MacOS.Desktop;
using SnapBoard.Platform.MacOS.Security;
using SnapBoard.Platform.Windows;
using SnapBoard.Platform.Windows.Clipboard;
using SnapBoard.Platform.Windows.Desktop;
using SnapBoard.Platform.Windows.Security;
using SnapBoard.Sync.WebDav;

namespace SnapBoard.Desktop.Bootstrap;

/// <summary>
/// 桌面进程唯一的依赖组合根。所有平台实现、基础设施和用例都应在这里显式注册，
/// 禁止使用程序集扫描，以免破坏 Native AOT 的可裁剪性和启动性能。
/// </summary>
internal static class DesktopCompositionRoot
{
    public static ServiceProvider Build(DesktopStorageStartupContext? storageStartup = null)
    {
        ServiceCollection services = new();
        AddClipboardHistoryServices(services, storageStartup);
        services.AddSingleton(provider => MainViewModel.CreateForServices(
            provider.GetRequiredService<IClipboardHistoryService>(),
            provider.GetService<IClipboardSourceApplicationMetadataResolver>(),
            provider.GetService<ISyncService>(),
            provider.GetService<IHistorySettingsService>()));

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

    private static void AddClipboardHistoryServices(
        IServiceCollection services,
        DesktopStorageStartupContext? storageStartup)
    {
        if (storageStartup is null)
        {
            services.AddSingleton(SnapBoardStoragePaths.CreateDefault());
        }
        else
        {
            services.AddSingleton(storageStartup.BootstrapPaths);
            services.AddSingleton(storageStartup.LocationStore);
            services.AddSingleton(storageStartup.ActiveLocation);
            services.AddSingleton(storageStartup.ActiveLocation.Paths);
            services.AddSingleton<IStoragePlatformService>(storageStartup.PlatformService);
            services.AddSingleton<IStorageManagementService>(storageStartup.ManagementService);
            if (storageStartup.MigrationId is not null)
            {
                services.AddSingleton(provider => new StorageStartupAcknowledgementCoordinator(
                    storageStartup.MigrationId,
                    provider.GetRequiredService<IClipboardHistoryService>(),
                    provider.GetRequiredService<IStorageManagementService>(),
                    provider.GetRequiredService<IStoragePlatformService>()));
            }
        }

        services.AddSingleton(provider => new SnapBoardDatabaseConnectionFactory(
            provider.GetRequiredService<SnapBoardStoragePaths>().DatabasePath));
        services.AddSingleton<SnapBoardDatabaseMigrator>();
        services.AddSingleton<SqliteClipboardHistoryStore>();
        services.AddSingleton<IClipboardHistoryStore>(provider =>
            provider.GetRequiredService<SqliteClipboardHistoryStore>());
        services.AddSingleton<ISyncStore>(provider =>
            provider.GetRequiredService<SqliteClipboardHistoryStore>());
        services.AddSingleton<IStorageMigrationBarrier>(provider =>
            provider.GetRequiredService<SqliteClipboardHistoryStore>());
        services.AddSingleton<ClipboardHistoryChangeNotifier>();
        services.AddSingleton<IClipboardHistoryService, ClipboardHistoryService>();

        services.AddSingleton(new ClipboardCaptureOptions());
        services.AddSingleton<IHistorySettingsService, HistorySettingsService>();
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
        AddSyncServices(services);
        services.AddSingleton<
            IClipboardSourceApplicationMetadataResolver,
            WindowsClipboardSourceApplicationMetadataResolver>();
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
        services.AddSingleton<IDesktopSystemEventService, MacOSDesktopSystemEventService>();
        services.AddSingleton<ILaunchContextService, MacOSLaunchContextService>();
        services.AddSingleton<IPlatformSecretStore, MacOSKeychainSecretStore>();
        AddSyncServices(services);
        services.AddSingleton<ClipboardCaptureCoordinator>();
    }

    private static void AddSyncServices(IServiceCollection services)
    {
        services.AddSingleton<ISyncKeyService, PlatformSyncKeyService>();
        services.AddSingleton<ISyncCredentialService, PlatformSyncCredentialService>();
        services.AddSingleton<ISyncRecoveryMaterialStore, FileSyncRecoveryMaterialStore>();
        services.AddSingleton<ISyncObjectProtector, SyncObjectProtector>();
        services.AddSingleton<WebDavSyncRemoteSessionFactory>();
        services.AddSingleton<ISyncRemoteSessionFactory>(provider =>
            provider.GetRequiredService<WebDavSyncRemoteSessionFactory>());
        services.AddSingleton<ISyncRemoteProviderMigrationSessionFactory>(provider =>
            provider.GetRequiredService<WebDavSyncRemoteSessionFactory>());
        services.AddSingleton<SyncService>();
        services.AddSingleton<ISyncService>(provider =>
            provider.GetRequiredService<SyncService>());
        services.AddSingleton<ISyncProviderMigrationService>(provider =>
            provider.GetRequiredService<SyncService>());
    }
}
