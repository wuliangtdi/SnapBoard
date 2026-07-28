using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Application.Sync;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Security;
using SnapBoard.Platform.MacOS;
using SnapBoard.Platform.MacOS.Security;
using SnapBoard.Platform.Windows;
using SnapBoard.Platform.Windows.Clipboard;

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
            Assert.Null(provider.GetService<IClipboardSourceApplicationMetadataResolver>());
            Assert.IsType<MacOSKeychainSecretStore>(
                provider.GetRequiredService<IPlatformSecretStore>());
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
            Assert.IsType<WindowsClipboardSourceApplicationMetadataResolver>(
                provider.GetRequiredService<IClipboardSourceApplicationMetadataResolver>());
            Assert.IsType<SyncService>(provider.GetRequiredService<ISyncService>());
            AssertSyncMigrationServices(provider);
        }
        else
        {
            Assert.Null(monitor);
        }
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
