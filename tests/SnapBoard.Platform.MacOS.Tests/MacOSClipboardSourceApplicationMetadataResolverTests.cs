using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Clipboard;
using SnapBoard.Platform.MacOS.Desktop;

namespace SnapBoard.Platform.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSClipboardSourceApplicationMetadataResolverTests
{
    [MacOSFact]
    public async Task ResolvesNativeBundleIconAndPreservesSourceDisplayName()
    {
        string? executablePath = FindInstalledApplicationExecutable();
        if (executablePath is null)
        {
            return;
        }

        MacOSClipboardSourceApplicationMetadataResolver resolver = new(
            DirectPlatformMainThreadDispatcher.Instance);
        ClipboardSourceApplicationMetadata metadata = await resolver.ResolveAsync(
            new ClipboardSourceApplicationIdentity("test-process", executablePath),
            CancellationToken.None);

        Assert.Equal("test-process", metadata.DisplayName);
        ClipboardSourceApplicationIcon icon = Assert.IsType<ClipboardSourceApplicationIcon>(
            metadata.Icon);
        Assert.InRange(icon.Width, 1, 256);
        Assert.InRange(icon.Height, 1, 256);
        Assert.Equal(icon.Width * 4, icon.Stride);
        Assert.Equal(icon.Stride * icon.Height, icon.BgraPixels.Length);
        Assert.Contains(icon.BgraPixels.ToArray(), pixel => pixel != 0);

        ClipboardSourceApplicationMetadata cached = await resolver.ResolveAsync(
            new ClipboardSourceApplicationIdentity("test-process", executablePath),
            CancellationToken.None);
        Assert.Same(metadata, cached);
    }

    [MacOSFact]
    public void FindsOnlyExistingEnclosingAppBundle()
    {
        string root = Path.Combine(Path.GetTempPath(), $"snapboard-{Guid.NewGuid():N}");
        string executable = Path.Combine(root, "Example.app", "Contents", "MacOS", "Example");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        try
        {
            Assert.Equal(
                Path.Combine(root, "Example.app"),
                MacOSClipboardSourceApplicationMetadataResolver.FindEnclosingAppBundle(executable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Assert.Null(
            MacOSClipboardSourceApplicationMetadataResolver.FindEnclosingAppBundle(
                "/tmp/does-not-exist.app/Contents/MacOS/Example"));
    }

    private static string? FindInstalledApplicationExecutable()
    {
        string[] bundles = Directory
            .EnumerateDirectories("/Applications", "*.app")
            .OrderBy(path => path.EndsWith("Safari.app", StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ToArray();
        foreach (string bundle in bundles)
        {
            string contents = Path.Combine(bundle, "Contents", "MacOS");
            string? executable = Directory.Exists(contents)
                ? Directory.EnumerateFiles(contents).FirstOrDefault(File.Exists)
                : null;
            if (executable is not null)
            {
                return executable;
            }
        }

        return null;
    }
}
