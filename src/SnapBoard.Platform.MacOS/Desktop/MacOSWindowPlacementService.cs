using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOSWindowPlacementService : IPlatformWindowPlacementService, IDisposable
{
    private const nuint FullScreenAuxiliary = 1u << 8;
    private const nuint MoveToActiveSpace = 1u << 1;
    private const int FloatingWindowLevel = 3;
    private const uint DefaultDpi = 96;

    private readonly IPlatformMainThreadDispatcher _dispatcher;
    private readonly IMacOSSettingsStore _settings;

    public MacOSWindowPlacementService(IPlatformMainThreadDispatcher dispatcher)
        : this(dispatcher, new MacOSSettingsStore())
    {
    }

    internal MacOSWindowPlacementService(
        IPlatformMainThreadDispatcher dispatcher,
        IMacOSSettingsStore settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
    }

    public PlatformScreenPlacement? CaptureForegroundScreen() =>
        _dispatcher.Invoke<PlatformScreenPlacement?>(() =>
            TryGetMainScreen(out ScreenFrame screen) ? screen.ToPlacement() : null);

    public bool CenterWindow(
        nint windowHandle,
        PlatformScreenPlacement screen,
        int widthInDeviceIndependentPixels,
        int heightInDeviceIndependentPixels) => _dispatcher.Invoke(() =>
    {
        if (windowHandle == 0 || widthInDeviceIndependentPixels <= 0 ||
            heightInDeviceIndependentPixels <= 0 || screen.Width <= 0 || screen.Height <= 0)
        {
            return false;
        }

        double width = Math.Min(widthInDeviceIndependentPixels, screen.Width);
        double height = Math.Min(heightInDeviceIndependentPixels, screen.Height);
        double x = screen.X + ((screen.Width - width) / 2d);
        double y = screen.Y + Math.Max(0d, (screen.Height - height) * 2d / 3d);

        MacOSNativeMethods.SendVoidWithNativeSize(
            windowHandle,
            ObjectiveC.GetSelector("setContentSize:"),
            new NativeSize(width, height));
        MacOSNativeMethods.SendVoidWithNativePoint(
            windowHandle,
            ObjectiveC.GetSelector("setFrameOrigin:"),
            new NativePoint(x, y));
        ConfigureForActiveSpace(windowHandle, floating: true);
        return true;
    });

    public bool TryRestore(nint windowHandle, string placementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementKey);
        return _dispatcher.Invoke(() => TryRestoreOnMainThread(windowHandle, placementKey));
    }

    public void Save(nint windowHandle, string placementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementKey);
        _dispatcher.Invoke(() =>
        {
            SaveOnMainThread(windowHandle, placementKey);
            return true;
        });
    }

    public bool TryActivate(nint windowHandle) => _dispatcher.Invoke(() =>
    {
        if (windowHandle == 0)
        {
            return false;
        }

        ConfigureForActiveSpace(windowHandle, floating: false);
        MacOSNativeMethods.SendVoidWithIntPtr(
            windowHandle,
            ObjectiveC.GetSelector("makeKeyAndOrderFront:"),
            0);
        nint application = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSApplication"),
            ObjectiveC.GetSelector("sharedApplication"));
        MacOSNativeMethods.SendVoidWithByte(
            application,
            ObjectiveC.GetSelector("activateIgnoringOtherApps:"),
            1);
        return true;
    });

    public void Dispose() => _settings.Dispose();

    private bool TryRestoreOnMainThread(nint windowHandle, string placementKey)
    {
        if (windowHandle == 0 || !StoredWindowPlacement.TryParse(
                _settings.GetString(placementKey),
                out StoredWindowPlacement stored) ||
            !TryGetNearestScreen(stored, out ScreenFrame screen))
        {
            return false;
        }

        double width = Math.Clamp(stored.Width, 320d, screen.Width);
        double height = Math.Clamp(stored.Height, 240d, screen.Height);
        double x = Math.Clamp(stored.X, screen.X, screen.X + screen.Width - width);
        double y = Math.Clamp(stored.Y, screen.Y, screen.Y + screen.Height - height);
        MacOSNativeMethods.SendVoidWithNativeRectangleByte(
            windowHandle,
            ObjectiveC.GetSelector("setFrame:display:"),
            new NativeRectangle(new NativePoint(x, y), new NativeSize(width, height)),
            0);
        if (stored.Zoomed &&
            MacOSNativeMethods.SendBool(windowHandle, ObjectiveC.GetSelector("isZoomed")) == 0)
        {
            MacOSNativeMethods.SendVoidWithIntPtr(
                windowHandle,
                ObjectiveC.GetSelector("zoom:"),
                0);
        }

        return true;
    }

    private void SaveOnMainThread(nint windowHandle, string placementKey)
    {
        if (windowHandle == 0 || !TryReadFrame(windowHandle, "frame", out ScreenFrame frame))
        {
            return;
        }

        bool zoomed = MacOSNativeMethods.SendBool(
            windowHandle,
            ObjectiveC.GetSelector("isZoomed")) != 0;
        StoredWindowPlacement placement = new(
            frame.X,
            frame.Y,
            frame.Width,
            frame.Height,
            frame.Dpi,
            zoomed);
        try
        {
            _settings.SetString(placementKey, placement.Serialize());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 窗口关闭不能被损坏的偏好或用户目录权限阻塞；本次只放弃位置保存。
        }
    }

    private static void ConfigureForActiveSpace(nint window, bool floating)
    {
        nuint behavior = MacOSNativeMethods.SendNUInt(
            window,
            ObjectiveC.GetSelector("collectionBehavior"));
        MacOSNativeMethods.SendVoidWithNUInt(
            window,
            ObjectiveC.GetSelector("setCollectionBehavior:"),
            behavior | MoveToActiveSpace | FullScreenAuxiliary);
        if (floating)
        {
            MacOSNativeMethods.SendVoidWithInt32(
                window,
                ObjectiveC.GetSelector("setLevel:"),
                FloatingWindowLevel);
        }
    }

    private static bool TryGetNearestScreen(
        StoredWindowPlacement stored,
        out ScreenFrame screen)
    {
        List<ScreenFrame> screens = GetScreens();
        if (screens.Count == 0)
        {
            screen = default;
            return false;
        }

        double centerX = stored.X + (stored.Width / 2d);
        double centerY = stored.Y + (stored.Height / 2d);
        foreach (ScreenFrame candidate in screens)
        {
            if (centerX >= candidate.X && centerX <= candidate.X + candidate.Width &&
                centerY >= candidate.Y && centerY <= candidate.Y + candidate.Height)
            {
                screen = candidate;
                return true;
            }
        }

        screen = screens.MinBy(candidate =>
        {
            double dx = centerX - (candidate.X + (candidate.Width / 2d));
            double dy = centerY - (candidate.Y + (candidate.Height / 2d));
            return (dx * dx) + (dy * dy);
        });
        return true;
    }

    private static bool TryGetMainScreen(out ScreenFrame screen)
    {
        using NativeAutoreleasePool pool = new();
        nint nativeScreen = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSScreen"),
            ObjectiveC.GetSelector("mainScreen"));
        return TryReadFrame(nativeScreen, "visibleFrame", out screen);
    }

    private static List<ScreenFrame> GetScreens()
    {
        using NativeAutoreleasePool pool = new();
        List<ScreenFrame> result = [];
        nint screens = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSScreen"),
            ObjectiveC.GetSelector("screens"));
        nuint count = MacOSNativeMethods.SendNUInt(screens, ObjectiveC.GetSelector("count"));
        for (nuint index = 0; index < count; index++)
        {
            nint screen = MacOSNativeMethods.SendIntPtrWithNUInt(
                screens,
                ObjectiveC.GetSelector("objectAtIndex:"),
                index);
            if (TryReadFrame(screen, "visibleFrame", out ScreenFrame frame))
            {
                result.Add(frame);
            }
        }

        return result;
    }

    private static bool TryReadFrame(nint target, string selectorName, out ScreenFrame frame)
    {
        if (target == 0)
        {
            frame = default;
            return false;
        }

        NativeRectangle rectangle = ReadRectangle(
            target,
            ObjectiveC.GetSelector(selectorName));
        double x = rectangle.Origin.X;
        double y = rectangle.Origin.Y;
        double width = rectangle.Size.Width;
        double height = rectangle.Size.Height;
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            width <= 0 || height <= 0)
        {
            frame = default;
            return false;
        }

        double scale = MacOSNativeMethods.SendDouble(
            target,
            ObjectiveC.GetSelector("backingScaleFactor"));
        if (!double.IsFinite(scale) || scale < 1d)
        {
            scale = 1d;
        }

        frame = new ScreenFrame(
            x,
            y,
            width,
            height,
            checked((uint)Math.Round(DefaultDpi * Math.Max(1d, scale))));
        return true;
    }

    private static NativeRectangle ReadRectangle(nint target, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            // Intel 的大结构体返回遵循 stret ABI；Apple Silicon 则直接从 objc_msgSend 返回。
            MacOSNativeMethods.SendNativeRectangleStret(
                out NativeRectangle rectangle,
                target,
                selector);
            return rectangle;
        }

        return MacOSNativeMethods.SendNativeRectangle(target, selector);
    }

    private readonly record struct ScreenFrame(
        double X,
        double Y,
        double Width,
        double Height,
        uint Dpi)
    {
        public PlatformScreenPlacement ToPlacement() => new(
            checked((int)Math.Round(X)),
            checked((int)Math.Round(Y)),
            checked((int)Math.Round(Width)),
            checked((int)Math.Round(Height)),
            Dpi);
    }

    private readonly record struct StoredWindowPlacement(
        double X,
        double Y,
        double Width,
        double Height,
        uint Dpi,
        bool Zoomed)
    {
        public string Serialize() => string.Create(
            CultureInfo.InvariantCulture,
            $"{X:R},{Y:R},{Width:R},{Height:R},{Dpi},{(Zoomed ? 1 : 0)}");

        public static bool TryParse(string? value, out StoredWindowPlacement placement)
        {
            placement = default;
            string[] parts = value?.Split(',', StringSplitOptions.TrimEntries) ?? [];
            if (parts.Length != 6 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double height) ||
                !uint.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dpi) ||
                !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int zoomed) ||
                !double.IsFinite(x) || !double.IsFinite(y) ||
                !double.IsFinite(width) || !double.IsFinite(height) ||
                width <= 0 || height <= 0 || dpi == 0 || zoomed is < 0 or > 1)
            {
                return false;
            }

            placement = new StoredWindowPlacement(x, y, width, height, dpi, zoomed == 1);
            return true;
        }
    }
}
