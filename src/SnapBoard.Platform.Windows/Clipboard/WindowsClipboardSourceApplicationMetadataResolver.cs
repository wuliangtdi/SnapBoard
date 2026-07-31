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
    IClipboardSourceApplicationMetadataResolver,
    IClipboardSourceApplicationIconProvider
{
    private const int IconSize = 32;
    private const int MaximumCacheEntries = 256;
    private const int MaximumConcurrentResolutions = 4;
    private static readonly Dictionary<string, string> KnownDisplayNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ApplicationFrameHost"] = "Windows 应用",
            ["chrome"] = "Google Chrome",
            ["Code"] = "Visual Studio Code",
            ["codex"] = "Codex",
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
    private static readonly Dictionary<string, string> KnownPackageDisplayNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.ScreenSketch_8wekyb3d8bbwe"] = "截图工具",
            ["OpenAI.Codex_2p2nqsd0c76g0"] = "Codex",
        };

    private readonly ConcurrentDictionary<
        string,
        Lazy<Task<ClipboardSourceApplicationMetadata>>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<bool> _resolutionSlots = CreateResolutionSlots();

    public async ValueTask<ClipboardSourceApplicationMetadata> ResolveAsync(
        ClipboardSourceApplicationIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();

        string fallbackName = ResolveFallbackName(identity);
        string? normalizedPath = NormalizeExecutablePath(identity.ExecutablePath);
        string? applicationUserModelId = NormalizeApplicationUserModelId(
            identity.ApplicationUserModelId);
        string? packageFamilyName = SanitizeIdentity(identity.PackageFamilyName);
        string? cacheKey = applicationUserModelId is not null
            ? $"app:{applicationUserModelId}"
            : normalizedPath is not null
                ? $"path:{normalizedPath}"
                : null;
        if (cacheKey is null)
        {
            return new ClipboardSourceApplicationMetadata(
                ResolveKnownPackageDisplayName(packageFamilyName) ?? fallbackName);
        }

        ClipboardSourceApplicationIdentity normalizedIdentity = identity with
        {
            ExecutablePath = normalizedPath,
            ApplicationUserModelId = applicationUserModelId,
            PackageFamilyName = packageFamilyName,
        };
        if (_cache.Count >= MaximumCacheEntries && !_cache.ContainsKey(cacheKey))
        {
            return await ResolveOnBackgroundAsync(normalizedIdentity, fallbackName)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        Lazy<Task<ClipboardSourceApplicationMetadata>> lazy = _cache.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<ClipboardSourceApplicationMetadata>>(
                () => ResolveOnBackgroundAsync(normalizedIdentity, fallbackName),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            ClipboardSourceApplicationMetadata metadata = await lazy.Value
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (metadata.Icon is null)
            {
                // Shell/GDI 在桌面启动和图标缓存刷新期间可能暂时取不到 HICON；
                // 空结果不能污染进程级缓存，否则同一应用在本次运行中永远只显示占位符。
                _cache.TryRemove(new KeyValuePair<
                    string,
                    Lazy<Task<ClipboardSourceApplicationMetadata>>>(cacheKey, lazy));
            }

            return metadata;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<
                string,
                Lazy<Task<ClipboardSourceApplicationMetadata>>>(cacheKey, lazy));
            return new ClipboardSourceApplicationMetadata(fallbackName);
        }
    }

    public async ValueTask<ClipboardSourceApplicationIcon?> CaptureAsync(
        ClipboardSourceApplicationIdentity identity,
        CancellationToken cancellationToken) =>
        (await ResolveAsync(identity, cancellationToken).ConfigureAwait(false)).Icon;

    private async Task<ClipboardSourceApplicationMetadata> ResolveOnBackgroundAsync(
        ClipboardSourceApplicationIdentity identity,
        string fallbackName)
    {
        // 可见页可能同时出现多个应用；限制后台 Shell/磁盘并发，避免滚动时冲击线程池和存储。
        await _resolutionSlots.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => ResolveCore(identity, fallbackName),
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
        ClipboardSourceApplicationIdentity identity,
        string fallbackName)
    {
        int comResult = WindowsNativeMethods.InitializeComApartment(
            0,
            WindowsNativeConstants.ComApartmentMultithreaded);
        bool uninitializeCom = comResult >= 0;
        try
        {
            ShellMetadata packaged = identity.ApplicationUserModelId is null
                ? default
                : ReadPackagedShellMetadata(identity.ApplicationUserModelId);
            ShellMetadata executable = identity.ExecutablePath is null
                ? default
                : ReadExecutableShellMetadata(identity.ExecutablePath);
            string? versionDisplayName = identity.ExecutablePath is null
                ? null
                : ReadVersionDisplayName(identity.ExecutablePath, identity.ProcessName);
            string? executableDisplayName = identity.ExecutablePath is null
                ? null
                : NormalizeShellDisplayName(
                    executable.DisplayName,
                    identity.ExecutablePath);
            string displayName = ResolveKnownPackageDisplayName(identity.PackageFamilyName) ??
                packaged.DisplayName ??
                ResolveKnownDisplayName(identity.ProcessName) ??
                versionDisplayName ??
                executableDisplayName ??
                fallbackName;
            return new ClipboardSourceApplicationMetadata(
                displayName,
                packaged.Icon ?? executable.Icon);
        }
        finally
        {
            if (uninitializeCom)
            {
                WindowsNativeMethods.UninitializeComApartment();
            }
        }
    }

    private static unsafe ShellMetadata ReadPackagedShellMetadata(string applicationUserModelId)
    {
        nint itemIdentifierList = 0;
        try
        {
            int result = WindowsNativeMethods.ParseShellDisplayName(
                $"shell:AppsFolder\\{applicationUserModelId}",
                0,
                out itemIdentifierList,
                0,
                null);
            if (result < 0 || itemIdentifierList == 0)
            {
                return default;
            }

            return ReadShellMetadata(
                itemIdentifierList,
                WindowsNativeConstants.ShellFileInfoItemIdentifierList);
        }
        finally
        {
            if (itemIdentifierList != 0)
            {
                // SHParseDisplayName 返回 CoTaskMem 所有权，成功和部分失败路径都必须释放。
                Marshal.FreeCoTaskMem(itemIdentifierList);
            }
        }
    }

    private static unsafe ShellMetadata ReadExecutableShellMetadata(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return default;
        }

        ShellFileInfo fileInfo = default;
        nuint result = WindowsNativeMethods.GetShellFileInfo(
            executablePath,
            0,
            &fileInfo,
            (uint)sizeof(ShellFileInfo),
            WindowsNativeConstants.ShellFileInfoIcon |
            WindowsNativeConstants.ShellFileInfoDisplayName |
            WindowsNativeConstants.ShellFileInfoLargeIcon);
        return result == 0 ? default : CreateShellMetadata(fileInfo);
    }

    private static unsafe ShellMetadata ReadShellMetadata(nint itemIdentifierList, uint extraFlags)
    {
        ShellFileInfo fileInfo = default;
        nuint result = WindowsNativeMethods.GetShellItemInfo(
            itemIdentifierList,
            0,
            &fileInfo,
            (uint)sizeof(ShellFileInfo),
            extraFlags |
            WindowsNativeConstants.ShellFileInfoIcon |
            WindowsNativeConstants.ShellFileInfoDisplayName |
            WindowsNativeConstants.ShellFileInfoLargeIcon);
        return result == 0 ? default : CreateShellMetadata(fileInfo);
    }

    private static unsafe ShellMetadata CreateShellMetadata(ShellFileInfo fileInfo)
    {
        char* displayNameBuffer = fileInfo.DisplayName;
        string? displayName = SanitizeDisplayName(new string(displayNameBuffer));
        ClipboardSourceApplicationIcon? icon = null;
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

        return new ShellMetadata(displayName, icon);
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

    private static string ResolveFallbackName(ClipboardSourceApplicationIdentity identity)
    {
        string normalizedProcessName = NormalizeProcessName(identity.ProcessName);
        if (ResolveKnownPackageDisplayName(identity.PackageFamilyName) is { } packageName)
        {
            return packageName;
        }

        if (ResolveKnownDisplayName(normalizedProcessName) is { } knownName)
        {
            return knownName;
        }

        if (normalizedProcessName.Length > 0)
        {
            return normalizedProcessName;
        }

        string? executableName = identity.ExecutablePath is null
            ? null
            : Path.GetFileNameWithoutExtension(identity.ExecutablePath);
        return string.IsNullOrWhiteSpace(executableName) ? "未知来源" : executableName;
    }

    private static string? ResolveKnownDisplayName(string processName)
    {
        string key = NormalizeProcessName(processName);
        return KnownDisplayNames.TryGetValue(key, out string? displayName)
            ? displayName
            : null;
    }

    private static string? ResolveKnownPackageDisplayName(string? packageFamilyName)
    {
        string? key = SanitizeIdentity(packageFamilyName);
        return key is not null && KnownPackageDisplayNames.TryGetValue(key, out string? displayName)
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

    private static string? NormalizeApplicationUserModelId(string? value)
    {
        string? identity = SanitizeIdentity(value);
        return identity is null ||
            !identity.Contains('!', StringComparison.Ordinal) ||
            identity.IndexOfAny(['\\', '/', ':']) >= 0
            ? null
            : identity;
    }

    private static string? SanitizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string identity = value.Trim();
        return identity.Length > 256 || identity.Any(char.IsControl)
            ? null
            : identity;
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

    private readonly record struct ShellMetadata(
        string? DisplayName,
        ClipboardSourceApplicationIcon? Icon);
}
