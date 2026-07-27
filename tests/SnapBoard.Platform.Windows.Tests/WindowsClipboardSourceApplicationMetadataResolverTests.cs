using SnapBoard.Platform.Abstractions.Clipboard;
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
            new ClipboardSourceApplicationIdentity(
                Path.GetFileNameWithoutExtension(executablePath),
                executablePath),
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
            new ClipboardSourceApplicationIdentity("Weixin"),
            CancellationToken.None);
        var workWeixin = await resolver.ResolveAsync(
            new ClipboardSourceApplicationIdentity("WXWork.exe"),
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
            new ClipboardSourceApplicationIdentity(processName, executablePath),
            CancellationToken.None)).Icon);
        uint before = WindowsNativeMethods.GetGuiResources(
            WindowsNativeMethods.GetCurrentProcess(),
            WindowsNativeConstants.GuiResourcesGdiObjects);
        Assert.NotEqual(0u, before);

        for (int iteration = 0; iteration < 64; iteration++)
        {
            WindowsClipboardSourceApplicationMetadataResolver resolver = new();
            Assert.NotNull((await resolver.ResolveAsync(
                new ClipboardSourceApplicationIdentity(processName, executablePath),
                CancellationToken.None)).Icon);
        }

        uint after = WindowsNativeMethods.GetGuiResources(
            WindowsNativeMethods.GetCurrentProcess(),
            WindowsNativeConstants.GuiResourcesGdiObjects);
        Assert.InRange(after, 1u, before + 2);
    }

    [WindowsFact]
    public async Task UsesAppsFolderIconAndKeepsCodexProductNameWhenInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string applicationUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
        string packageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "OpenAI.Codex_2p2nqsd0c76g0");
        if (!Directory.Exists(packageDirectory))
        {
            return;
        }

        WindowsClipboardSourceApplicationMetadataResolver resolver = new();
        ClipboardSourceApplicationMetadata metadata = await resolver.ResolveAsync(
            new ClipboardSourceApplicationIdentity(
                "codex",
                ApplicationUserModelId: applicationUserModelId,
                PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0"),
            CancellationToken.None);

        Assert.Equal("Codex", metadata.DisplayName);
        Assert.NotNull(metadata.Icon);
        Assert.Contains(metadata.Icon.BgraPixels.ToArray(), value => value != 0);
    }

    [WindowsFact]
    public async Task UsesAppsFolderIconAndLocalizedNameForSnippingToolWhenInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string packageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Microsoft.ScreenSketch_8wekyb3d8bbwe");
        if (!Directory.Exists(packageDirectory))
        {
            return;
        }

        WindowsClipboardSourceApplicationMetadataResolver resolver = new();
        ClipboardSourceApplicationMetadata metadata = await resolver.ResolveAsync(
            new ClipboardSourceApplicationIdentity(
                "SnippingTool",
                ApplicationUserModelId: "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
                PackageFamilyName: "Microsoft.ScreenSketch_8wekyb3d8bbwe"),
            CancellationToken.None);

        Assert.Equal("截图工具", metadata.DisplayName);
        Assert.NotNull(metadata.Icon);
        Assert.Contains(metadata.Icon.BgraPixels.ToArray(), value => value != 0);
    }

    [WindowsFact]
    public void ReadsAumidAndPackageFamilyFromRunningCodexProcessWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string packageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "OpenAI.Codex_2p2nqsd0c76g0");
        if (!Directory.Exists(packageDirectory))
        {
            return;
        }

        System.Diagnostics.Process[] processes = System.Diagnostics.Process.GetProcessesByName(
            "codex");
        try
        {
            ClipboardSourceInfo? source = processes
                .Select(process => WindowsClipboardReader.ReadProcessSource(
                    process.Id,
                    ClipboardSourceAttributionKind.ForegroundWindowAtChange))
                .FirstOrDefault(candidate => string.Equals(
                    candidate.PackageFamilyName,
                    "OpenAI.Codex_2p2nqsd0c76g0",
                    StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(source);
            Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!App", source.ApplicationUserModelId);
            Assert.Equal(ClipboardSourceAccessStatus.Identified, source.AccessStatus);
            Assert.Equal(
                ClipboardSourceAttributionKind.ForegroundWindowAtChange,
                source.AttributionKind);
        }
        finally
        {
            foreach (System.Diagnostics.Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}
