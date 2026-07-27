using SnapBoard.Platform.Windows.Clipboard;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Tests;

[Collection(WindowsClipboardNativeIntegrationTests.CollectionName)]
public sealed class WindowsClipboardSourceApplicationMetadataResolverTests
{
    [WindowsFact]
    public async Task ResolvesCurrentExecutableDisplayNameAndRealShellIcon()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string executablePath = Assert.IsType<string>(Environment.ProcessPath);
        WindowsClipboardSourceApplicationMetadataResolver resolver = new();

        var metadata = await resolver.ResolveAsync(
            Path.GetFileNameWithoutExtension(executablePath),
            executablePath,
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(metadata.DisplayName));
        Assert.NotNull(metadata.Icon);
        Assert.Equal(32, metadata.Icon.Width);
        Assert.Equal(32, metadata.Icon.Height);
        Assert.Equal(32 * 4, metadata.Icon.Stride);
        Assert.Equal(32 * 32 * 4, metadata.Icon.BgraPixels.Length);
        Assert.Contains(metadata.Icon.BgraPixels.ToArray(), value => value != 0);
    }

    [WindowsFact]
    public async Task UsesLocalizedKnownNamesWhenExecutablePathIsUnavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WindowsClipboardSourceApplicationMetadataResolver resolver = new();

        var weixin = await resolver.ResolveAsync(
            "Weixin",
            null,
            CancellationToken.None);
        var workWeixin = await resolver.ResolveAsync(
            "WXWork.exe",
            null,
            CancellationToken.None);

        Assert.Equal("微信", weixin.DisplayName);
        Assert.Equal("企业微信", workWeixin.DisplayName);
        Assert.Null(weixin.Icon);
        Assert.Null(workWeixin.Icon);
    }

    [WindowsFact]
    public async Task RepeatedShellIconExtractionDoesNotLeakGdiObjects()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string executablePath = Assert.IsType<string>(Environment.ProcessPath);
        string processName = Path.GetFileNameWithoutExtension(executablePath);
        WindowsClipboardSourceApplicationMetadataResolver warmup = new();
        Assert.NotNull((await warmup.ResolveAsync(
            processName,
            executablePath,
            CancellationToken.None)).Icon);
        uint before = WindowsNativeMethods.GetGuiResources(
            WindowsNativeMethods.GetCurrentProcess(),
            WindowsNativeConstants.GuiResourcesGdiObjects);
        Assert.NotEqual(0u, before);

        for (int iteration = 0; iteration < 64; iteration++)
        {
            WindowsClipboardSourceApplicationMetadataResolver resolver = new();
            Assert.NotNull((await resolver.ResolveAsync(
                processName,
                executablePath,
                CancellationToken.None)).Icon);
        }

        uint after = WindowsNativeMethods.GetGuiResources(
            WindowsNativeMethods.GetCurrentProcess(),
            WindowsNativeConstants.GuiResourcesGdiObjects);
        Assert.InRange(after, 1u, before + 2);
    }
}
