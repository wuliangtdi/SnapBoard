using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Application.Sync;
using SnapBoard.Application.Updates;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Abstractions.Security;
using SnapBoard.Platform.MacOS;
using SnapBoard.Platform.MacOS.Clipboard;
using SnapBoard.Platform.MacOS.Desktop;
using SnapBoard.Platform.MacOS.Security;
using SnapBoard.Platform.Windows;
using SnapBoard.Platform.Windows.Clipboard;
using SnapBoard.Update.Velopack;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class DesktopCompositionRootTests
{
    [Fact]
    public void CurrentPlatformClipboardPortsResolveToOneAdapterInstance()
    {
        using ServiceProvider provider = DesktopCompositionRoot.Build();
        IClipboardMonitor? monitor = provider.GetService<IClipboardMonitor>();

        if (OperatingSystem.IsMacOS())
        {
            MacOSClipboardAdapter adapter = Assert.IsType<MacOSClipboardAdapter>(monitor);
            Assert.Same(adapter, provider.GetRequiredService<IClipboardContentReader>());
            Assert.Same(adapter, provider.GetRequiredService<IClipboardWriter>());
            Assert.Same(adapter, provider.GetRequiredService<IAutomaticPasteService>());
            IClipboardSourceApplicationMetadataResolver resolver =
                Assert.IsType<MacOSClipboardSourceApplicationMetadataResolver>(
                    provider.GetRequiredService<IClipboardSourceApplicationMetadataResolver>());
            Assert.Same(
                resolver,
                provider.GetRequiredService<IClipboardSourceApplicationIconProvider>());
            Assert.IsType<MacOSKeychainSecretStore>(
                provider.GetRequiredService<IPlatformSecretStore>());
            Assert.IsType<MacOSDesktopSystemEventService>(
                provider.GetRequiredService<IDesktopSystemEventService>());
            Assert.IsType<SyncService>(provider.GetRequiredService<ISyncService>());
            Assert.True(provider.GetRequiredService<MainViewModel>().HasSyncService);
            AssertSyncMigrationServices(provider);
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsClipboardAdapter adapter = Assert.IsType<WindowsClipboardAdapter>(monitor);
            Assert.Same(adapter, provider.GetRequiredService<IClipboardContentReader>());
            Assert.Same(adapter, provider.GetRequiredService<IClipboardWriter>());
            Assert.Same(adapter, provider.GetRequiredService<IAutomaticPasteService>());
            IClipboardSourceApplicationMetadataResolver resolver =
                Assert.IsType<WindowsClipboardSourceApplicationMetadataResolver>(
                    provider.GetRequiredService<IClipboardSourceApplicationMetadataResolver>());
            Assert.Same(
                resolver,
                provider.GetRequiredService<IClipboardSourceApplicationIconProvider>());
            Assert.IsType<SyncService>(provider.GetRequiredService<ISyncService>());
            AssertSyncMigrationServices(provider);
        }
        else
        {
            Assert.Null(monitor);
        }

        Assert.IsType<VelopackApplicationUpdateService>(
            provider.GetRequiredService<IApplicationUpdateService>());
        Assert.Same(
            provider.GetRequiredService<IApplicationUpdateService>(),
            provider.GetRequiredService<IApplicationUpdateService>());
    }

    private static void AssertSyncMigrationServices(ServiceProvider provider)
    {
        Assert.Same(
            provider.GetRequiredService<ISyncService>(),
            provider.GetRequiredService<ISyncProviderMigrationService>());
        Assert.Same(
            provider.GetRequiredService<ISyncRemoteSessionFactory>(),
            provider.GetRequiredService<ISyncRemoteProviderMigrationSessionFactory>());
    }
}
