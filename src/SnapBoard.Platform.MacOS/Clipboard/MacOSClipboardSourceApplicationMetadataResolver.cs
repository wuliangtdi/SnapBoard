using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Clipboard;

/// <summary>
/// 在平台主线程读取 macOS App Bundle 的本地化名称与原生图标。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSClipboardSourceApplicationMetadataResolver(
    IPlatformMainThreadDispatcher dispatcher) :
    IClipboardSourceApplicationMetadataResolver,
    IClipboardSourceApplicationIconProvider
{
    private const int IconSize = 32;
    private const int MaximumCacheEntries = 256;
    private const uint AlphaPremultipliedFirst = 2;
    private const uint ByteOrder32Little = 2 << 12;

    private readonly Dictionary<string, ClipboardSourceApplicationMetadata> _cache =
        new(StringComparer.Ordinal);
    private readonly IPlatformMainThreadDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private NativeBindings? _bindings;

    public ValueTask<ClipboardSourceApplicationMetadata> ResolveAsync(
        ClipboardSourceApplicationIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using NativeAutoreleasePool pool = new();
                return ResolveCore(identity);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and not StackOverflowException)
            {
                return new ClipboardSourceApplicationMetadata(
                    SanitizeDisplayName(identity.ProcessName) ?? "未知来源");
            }
        }, cancellationToken);
    }

    public async ValueTask<ClipboardSourceApplicationIcon?> CaptureAsync(
        ClipboardSourceApplicationIdentity identity,
        CancellationToken cancellationToken) =>
        (await ResolveAsync(identity, cancellationToken).ConfigureAwait(false)).Icon;

    internal static string? FindEnclosingAppBundle(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath))
        {
            return null;
        }

        try
        {
            DirectoryInfo? directory = new(Path.GetDirectoryName(executablePath)!);
            while (directory is not null)
            {
                if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    return directory.Exists ? directory.FullName : null;
                }

                directory = directory.Parent;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return null;
    }

    private ClipboardSourceApplicationMetadata ResolveCore(
        ClipboardSourceApplicationIdentity identity)
    {
        string fallbackName = SanitizeDisplayName(identity.ProcessName) ?? "未知来源";
        string? appBundlePath = FindEnclosingAppBundle(identity.ExecutablePath);
        if (appBundlePath is null)
        {
            return new ClipboardSourceApplicationMetadata(fallbackName);
        }

        if (_cache.TryGetValue(appBundlePath, out ClipboardSourceApplicationMetadata? cached))
        {
            return cached;
        }

        NativeBindings bindings = _bindings ??= new NativeBindings();
        nint workspace = MacOSNativeMethods.SendIntPtr(
            bindings.WorkspaceClass,
            bindings.SharedWorkspaceSelector);
        if (workspace == 0)
        {
            return new ClipboardSourceApplicationMetadata(fallbackName);
        }

        nint nativePath = ObjectiveC.CreateString(appBundlePath);
        if (nativePath == 0)
        {
            return new ClipboardSourceApplicationMetadata(fallbackName);
        }

        try
        {
            nint image = MacOSNativeMethods.SendIntPtrWithIntPtr(
                workspace,
                bindings.IconForFileSelector,
                nativePath);
            ClipboardSourceApplicationMetadata metadata = new(
                fallbackName,
                RenderIcon(image, bindings));
            if (metadata.Icon is not null && _cache.Count < MaximumCacheEntries)
            {
                // 可见页刷新会为同一来源创建新 ViewModel；缓存固定尺寸像素，避免重复访问
                // NSWorkspace 和反复分配图标缓冲。空图标不缓存，保留瞬态失败重试能力。
                _cache.TryAdd(appBundlePath, metadata);
            }

            return metadata;
        }
        finally
        {
            ObjectiveC.Release(nativePath);
        }
    }

    private static ClipboardSourceApplicationIcon? RenderIcon(
        nint image,
        NativeBindings bindings)
    {
        if (image == 0)
        {
            return null;
        }

        nint cgImage = MacOSNativeMethods.SendIntPtrWithIntPtrIntPtrIntPtr(
            image,
            bindings.CgImageForProposedRectSelector,
            0,
            0,
            0);
        ClipboardSourceApplicationIcon? rendered = RenderCgImage(cgImage);
        if (rendered is not null)
        {
            return rendered;
        }

        nint tiffData = MacOSNativeMethods.SendIntPtr(
            image,
            bindings.TiffRepresentationSelector);
        if (tiffData == 0)
        {
            return null;
        }

        nint imageSource = MacOSNativeMethods.CGImageSourceCreateWithData(tiffData, 0);
        if (imageSource == 0)
        {
            return null;
        }

        nint decodedImage = 0;
        try
        {
            decodedImage = MacOSNativeMethods.CGImageSourceCreateImageAtIndex(
                imageSource,
                0,
                0);
            return RenderCgImage(decodedImage);
        }
        finally
        {
            if (decodedImage != 0)
            {
                MacOSNativeMethods.CFRelease(decodedImage);
            }

            MacOSNativeMethods.CFRelease(imageSource);
        }
    }

    private static ClipboardSourceApplicationIcon? RenderCgImage(nint cgImage)
    {
        if (cgImage == 0 ||
            MacOSNativeMethods.CGImageGetWidth(cgImage) == 0 ||
            MacOSNativeMethods.CGImageGetHeight(cgImage) == 0)
        {
            return null;
        }

        int stride = checked(IconSize * 4);
        int byteCount = checked(stride * IconSize);
        nint colorSpace = MacOSNativeMethods.CGColorSpaceCreateDeviceRGB();
        if (colorSpace == 0)
        {
            return null;
        }

        nint context = 0;
        nint allocatedPixels = 0;
        try
        {
            byte[] pixels = new byte[byteCount];
            allocatedPixels = Marshal.AllocHGlobal(byteCount);
            Marshal.Copy(pixels, 0, allocatedPixels, pixels.Length);
            context = MacOSNativeMethods.CGBitmapContextCreate(
                allocatedPixels,
                IconSize,
                IconSize,
                8,
                (nuint)stride,
                colorSpace,
                AlphaPremultipliedFirst | ByteOrder32Little);
            if (context == 0)
            {
                return null;
            }

            MacOSNativeMethods.CGContextDrawImage(
                context,
                new NativeRectangle(
                    new NativePoint(0, 0),
                    new NativeSize(IconSize, IconSize)),
                cgImage);
            nint data = MacOSNativeMethods.CGBitmapContextGetData(context);
            if (data == 0)
            {
                return null;
            }

            Marshal.Copy(data, pixels, 0, pixels.Length);
            return new ClipboardSourceApplicationIcon(IconSize, IconSize, stride, pixels);
        }
        finally
        {
            if (context != 0)
            {
                MacOSNativeMethods.CGContextRelease(context);
            }

            MacOSNativeMethods.CGColorSpaceRelease(colorSpace);
            if (allocatedPixels != 0)
            {
                Marshal.FreeHGlobal(allocatedPixels);
            }
        }
    }

    private static string? SanitizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string displayName = new(value
            .Trim()
            .Where(character => !char.IsControl(character))
            .Take(128)
            .ToArray());
        return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
    }

    private sealed class NativeBindings
    {
        public NativeBindings()
        {
            MacOSAppKit.EnsureInitialized();
            WorkspaceClass = ObjectiveC.GetRequiredClass("NSWorkspace");
            CgImageForProposedRectSelector = ObjectiveC.GetSelector(
                "CGImageForProposedRect:context:hints:");
            IconForFileSelector = ObjectiveC.GetSelector("iconForFile:");
            SharedWorkspaceSelector = ObjectiveC.GetSelector("sharedWorkspace");
            TiffRepresentationSelector = ObjectiveC.GetSelector("TIFFRepresentation");
        }

        public nint CgImageForProposedRectSelector { get; }

        public nint IconForFileSelector { get; }

        public nint SharedWorkspaceSelector { get; }

        public nint TiffRepresentationSelector { get; }

        public nint WorkspaceClass { get; }
    }
}
