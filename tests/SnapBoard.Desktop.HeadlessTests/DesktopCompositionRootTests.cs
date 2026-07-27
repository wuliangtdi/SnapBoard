using Microsoft.Extensions.DependencyInjection;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS;
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
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsClipboardAdapter adapter = Assert.IsType<WindowsClipboardAdapter>(monitor);
            Assert.Same(adapter, provider.GetRequiredService<IClipboardContentReader>());
            Assert.Same(adapter, provider.GetRequiredService<IClipboardWriter>());
            Assert.Same(adapter, provider.GetRequiredService<IAutomaticPasteService>());
            Assert.IsType<WindowsClipboardSourceApplicationMetadataResolver>(
                provider.GetRequiredService<IClipboardSourceApplicationMetadataResolver>());
        }
        else
        {
            Assert.Null(monitor);
        }
    }
}
