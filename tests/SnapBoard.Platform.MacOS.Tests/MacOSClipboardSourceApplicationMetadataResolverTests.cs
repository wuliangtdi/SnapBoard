using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Clipboard;

namespace SnapBoard.Platform.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSClipboardSourceApplicationMetadataResolverTests
{
    [MacOSFact]
    public async Task CapturesCanonicalTextEditAndFinderIconsOnMainThreadAndReusesCache()
    {
        (string ProcessName, string ExecutablePath)[] applications =
        [
            ("TextEdit", "/System/Applications/TextEdit.app/Contents/MacOS/TextEdit"),
            ("Finder", "/System/Library/CoreServices/Finder.app/Contents/MacOS/Finder"),
        ];
        RecordingPlatformMainThreadDispatcher dispatcher = new();
        MacOSClipboardSourceApplicationMetadataResolver resolver = new(dispatcher);

        foreach ((string processName, string executablePath) in applications)
        {
            Assert.True(File.Exists(executablePath));
            ClipboardSourceApplicationIdentity identity = new(processName, executablePath);
            ClipboardSourceApplicationMetadata metadata = await resolver.ResolveAsync(
                identity,
                CancellationToken.None);

            Assert.Equal(processName, metadata.DisplayName);
            ClipboardSourceApplicationIcon icon = Assert.IsType<ClipboardSourceApplicationIcon>(
                metadata.Icon);
            Assert.True(ClipboardSourceApplicationIconRules.IsCanonical(icon));
            Assert.Contains(icon.BgraPixels.ToArray(), pixel => pixel != 0);

            ClipboardSourceApplicationIcon captured =
                Assert.IsType<ClipboardSourceApplicationIcon>(
                    await resolver.CaptureAsync(identity, CancellationToken.None));
            Assert.Same(icon, captured);

            ClipboardSourceApplicationMetadata cached = await resolver.ResolveAsync(
                identity,
                CancellationToken.None);
            Assert.Same(metadata, cached);
        }

        Assert.Equal(applications.Length * 3, dispatcher.AsyncInvocationCount);
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

    private sealed class RecordingPlatformMainThreadDispatcher : IPlatformMainThreadDispatcher
    {
        public int AsyncInvocationCount { get; private set; }

        public bool CheckAccess() => true;

        public T Invoke<T>(Func<T> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            return operation();
        }

        public ValueTask<T> InvokeAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            AsyncInvocationCount++;
            return ValueTask.FromResult(operation());
        }
    }
}
