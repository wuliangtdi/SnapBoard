using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Clipboard;

[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardSourceApplicationMetadataResolver :
    IClipboardSourceApplicationMetadataResolver
{
    private const int IconSize = 32;
    private const int MaximumCacheEntries = 256;
    private const int MaximumConcurrentResolutions = 4;
    private static readonly Dictionary<string, string> KnownDisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ApplicationFrameHost"] = "Windows 应用",
            ["chrome"] = "Google Chrome",
            ["Code"] = "Visual Studio Code",
            ["EXCEL"] = "Microsoft Excel",
            ["explorer"] = "文件资源管理器",
            ["firefox"] = "Mozilla Firefox",
            ["msedge"] = "Microsoft Edge",
            ["notepad"] = "记事本",
            ["OUTLOOK"] = "Microsoft Outlook",
            ["POWERPNT"] = "Microsoft PowerPoint",
            ["SnippingTool"] = "截图工具",
            ["Weixin"] = "微信",
            ["WINWORD"] = "Microsoft Word",
            ["WindowsTerminal"] = "Windows 终端",
            ["WXWork"] = "企业微信",
        };

    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<ClipboardSourceApplicationMetadata>>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _resolutionSlots = CreateResolutionSlots();

    public async ValueTask<ClipboardSourceApplicationMetadata> ResolveAsync(
        string processName,
        string? executablePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fallbackName = ResolveFallbackName(processName, executablePath);
        string? normalizedPath = NormalizeExecutablePath(executablePath);
        if (normalizedPath is null)
        {
            return new ClipboardSourceApplicationMetadata(fallbackName);
        }

        if (_cache.Count >= MaximumCacheEntries && !_cache.ContainsKey(normalizedPath))
        {
            return await ResolveOnBackgroundAsync(processName, normalizedPath, fallbackName)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        Lazy<Task<ClipboardSourceApplicationMetadata>> lazy = _cache.GetOrAdd(
            normalizedPath,
            path => new Lazy<Task<ClipboardSourceApplicationMetadata>>(
                () => ResolveOnBackgroundAsync(processName, path, fallbackName),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<
                string,
                Lazy<Task<ClipboardSourceApplicationMetadata>>>(normalizedPath, lazy));
            return new ClipboardSourceApplicationMetadata(fallbackName);
        }
    }

    private async Task<ClipboardSourceApplicationMetadata> ResolveOnBackgroundAsync(
        string processName,
        string executablePath,
        string fallbackName)
    {
        // 可见页可能同时出现多个应用；限制后台 Shell/磁盘并发，避免滚动时冲击线程池和存储。
        await _resolutionSlots.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => ResolveCore(processName, executablePath, fallbackName),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _ = _resolutionSlots.Writer.TryWrite(true);
        }
    }

    private static Channel<bool> CreateResolutionSlots()
    {
        Channel<bool> slots = Channel.CreateBounded<bool>(new BoundedChannelOptions(
            MaximumConcurrentResolutions)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        for (int index = 0; index < MaximumConcurrentResolutions; index++)
        {
            if (!slots.Writer.TryWrite(true))
            {
                throw new InvalidOperationException("Unable to initialize source metadata slots.");
            }
        }

        return slots;
    }

    private static ClipboardSourceApplicationMetadata ResolveCore(
        string processName,
        string executablePath,
        string fallbackName)
    {
        if (!File.Exists(executablePath))
        {
            return new ClipboardSourceApplicationMetadata(fallbackName);
        }

        string? versionDisplayName = ReadVersionDisplayName(executablePath, processName);
        string? shellDisplayName = null;
        ClipboardSourceApplicationIcon? icon = null;
        ShellFileInfo fileInfo = default;
        unsafe
        {
            nuint result = WindowsNativeMethods.GetShellFileInfo(
                executablePath,
                0,
                &fileInfo,
                (uint)sizeof(ShellFileInfo),
                WindowsNativeConstants.ShellFileInfoIcon |
                WindowsNativeConstants.ShellFileInfoDisplayName |
                WindowsNativeConstants.ShellFileInfoLargeIcon);
            if (result != 0)
            {
                char* shellDisplayNameBuffer = fileInfo.DisplayName;
                shellDisplayName = SanitizeDisplayName(new string(shellDisplayNameBuffer));

                if (fileInfo.IconHandle != 0)
                {
                    try
                    {
                        icon = RenderIcon(fileInfo.IconHandle);
                    }
                    finally
                    {
                        // SHGetFileInfo 返回的 HICON 归调用方所有，任何栅格化结果都必须释放。
                        WindowsNativeMethods.DestroyIcon(fileInfo.IconHandle);
                    }
                }
            }
        }

        string displayName = ResolveKnownDisplayName(processName) ??
            versionDisplayName ??
            NormalizeShellDisplayName(shellDisplayName, executablePath) ??
            fallbackName;
        return new ClipboardSourceApplicationMetadata(displayName, icon);
    }

    private static string? ReadVersionDisplayName(string executablePath, string processName)
    {
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(executablePath);
            string? description = NormalizeVersionDisplayName(
                version.FileDescription,
                processName,
                executablePath);
            return description ?? NormalizeVersionDisplayName(
                version.ProductName,
                processName,
                executablePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? NormalizeVersionDisplayName(
        string? value,
        string processName,
        string executablePath)
    {
        string? displayName = SanitizeDisplayName(value);
        if (displayName is null)
        {
            return null;
        }

        string processFileName = NormalizeProcessName(processName);
        string executableFileName = Path.GetFileName(executablePath);
        return displayName.Equals(processFileName, StringComparison.OrdinalIgnoreCase) ||
            displayName.Equals(executableFileName, StringComparison.OrdinalIgnoreCase)
            ? null
            : displayName;
    }

    private static string? NormalizeShellDisplayName(string? value, string executablePath)
    {
        string? displayName = SanitizeDisplayName(value);
        return displayName is null ||
            displayName.Equals(Path.GetFileName(executablePath), StringComparison.OrdinalIgnoreCase)
            ? null
            : displayName;
    }

    private static string ResolveFallbackName(string processName, string? executablePath)
    {
        string normalizedProcessName = NormalizeProcessName(processName);
        if (ResolveKnownDisplayName(normalizedProcessName) is { } knownName)
        {
            return knownName;
        }

        if (normalizedProcessName.Length > 0)
        {
            return normalizedProcessName;
        }

        string? executableName = executablePath is null
            ? null
            : Path.GetFileNameWithoutExtension(executablePath);
        return string.IsNullOrWhiteSpace(executableName) ? "未知来源" : executableName;
    }

    private static string? ResolveKnownDisplayName(string processName)
    {
        string key = NormalizeProcessName(processName);
        return KnownDisplayNames.TryGetValue(key, out string? displayName)
            ? displayName
            : null;
    }

    private static string NormalizeProcessName(string processName)
    {
        string value = SanitizeDisplayName(processName) ?? string.Empty;
        try
        {
            return Path.GetFileNameWithoutExtension(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    private static string? NormalizeExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
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

    private static unsafe ClipboardSourceApplicationIcon? RenderIcon(nint iconHandle)
    {
        nint screenDeviceContext = 0;
        nint memoryDeviceContext = 0;
        nint bitmap = 0;
        nint previousBitmap = 0;
        try
        {
            screenDeviceContext = WindowsNativeMethods.GetDeviceContext(0);
            if (screenDeviceContext == 0)
            {
                return null;
            }

            memoryDeviceContext = WindowsNativeMethods.CreateCompatibleDeviceContext(
                screenDeviceContext);
            if (memoryDeviceContext == 0)
            {
                return null;
            }

            NativeBitmapInfo bitmapInfo = new()
            {
                Header = new NativeBitmapInfoHeader
                {
                    Size = (uint)sizeof(NativeBitmapInfoHeader),
                    Width = IconSize,
                    Height = -IconSize,
                    Planes = 1,
                    BitsPerPixel = 32,
                    Compression = WindowsNativeConstants.BitmapCompressionRgb,
                    ImageSize = IconSize * IconSize * 4,
                },
            };
            bitmap = WindowsNativeMethods.CreateDeviceIndependentBitmapSection(
                screenDeviceContext,
                &bitmapInfo,
                WindowsNativeConstants.DibRgbColors,
                out nint pixelBits,
                0,
                0);
            if (bitmap == 0 || pixelBits == 0)
            {
                return null;
            }

            previousBitmap = WindowsNativeMethods.SelectGraphicsObject(
                memoryDeviceContext,
                bitmap);
            if (previousBitmap == 0 || previousBitmap == new nint(-1))
            {
                return null;
            }

            int byteCount = IconSize * IconSize * 4;
            new Span<byte>((void*)pixelBits, byteCount).Clear();
            if (!WindowsNativeMethods.DrawIcon(
                    memoryDeviceContext,
                    0,
                    0,
                    iconHandle,
                    IconSize,
                    IconSize,
                    0,
                    0,
                    WindowsNativeConstants.DrawIconNormal))
            {
                return null;
            }

            byte[] pixels = new byte[byteCount];
            Marshal.Copy(pixelBits, pixels, 0, pixels.Length);
            if (!NormalizeAlpha(pixels))
            {
                return null;
            }

            return new ClipboardSourceApplicationIcon(
                IconSize,
                IconSize,
                IconSize * 4,
                pixels);
        }
        finally
        {
            // GDI 对象必须先从 DC 中恢复，再按与创建相反的顺序释放，避免长期历史列表泄漏句柄。
            if (previousBitmap != 0 && previousBitmap != new nint(-1) && memoryDeviceContext != 0)
            {
                WindowsNativeMethods.SelectGraphicsObject(memoryDeviceContext, previousBitmap);
            }

            if (bitmap != 0)
            {
                WindowsNativeMethods.DeleteGraphicsObject(bitmap);
            }

            if (memoryDeviceContext != 0)
            {
                WindowsNativeMethods.DeleteDeviceContext(memoryDeviceContext);
            }

            if (screenDeviceContext != 0)
            {
                _ = WindowsNativeMethods.ReleaseDeviceContext(0, screenDeviceContext);
            }
        }
    }

    private static bool NormalizeAlpha(Span<byte> pixels)
    {
        bool hasAlpha = false;
        bool hasColor = false;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            hasAlpha |= pixels[index + 3] != 0;
            hasColor |= pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0;
        }

        if (!hasAlpha && hasColor)
        {
            // 少数旧式图标只写颜色掩码；至少保留有色像素，透明背景仍保持透明。
            for (int index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0)
                {
                    pixels[index + 3] = byte.MaxValue;
                }
            }

            hasAlpha = true;
        }

        return hasAlpha;
    }
}
