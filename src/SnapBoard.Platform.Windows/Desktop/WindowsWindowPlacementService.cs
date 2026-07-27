using System.Globalization;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Desktop;

[SupportedOSPlatform("windows")]
public sealed class WindowsWindowPlacementService : IPlatformWindowPlacementService
{
    private const string SettingsSubKey = @"Software\SnapBoard\Desktop";
    private const uint DefaultDpi = 96;
    private readonly IWindowsRegistryStore _registry;

    public WindowsWindowPlacementService()
        : this(new WindowsRegistryStore())
    {
    }

    internal WindowsWindowPlacementService(IWindowsRegistryStore registry)
    {
        _registry = registry;
    }

    public PlatformScreenPlacement? CaptureForegroundScreen()
    {
        nint targetWindow = WindowsNativeMethods.GetForegroundWindow();
        if (targetWindow == 0 || !WindowsNativeMethods.IsWindow(targetWindow))
        {
            return null;
        }

        nint monitor = WindowsNativeMethods.MonitorFromWindow(
            targetWindow,
            WindowsNativeConstants.MonitorDefaultToNearest);
        if (!TryGetWorkArea(monitor, out NativeRectangle workArea))
        {
            return null;
        }

        uint dpi = WindowsNativeMethods.GetDpiForWindow(targetWindow);
        return new PlatformScreenPlacement(
            workArea.Left,
            workArea.Top,
            workArea.Right - workArea.Left,
            workArea.Bottom - workArea.Top,
            dpi == 0 ? DefaultDpi : dpi);
    }

    public bool CenterWindow(
        nint windowHandle,
        PlatformScreenPlacement screen,
        int widthInDeviceIndependentPixels,
        int heightInDeviceIndependentPixels)
    {
        if (!WindowsNativeMethods.IsWindow(windowHandle) ||
            widthInDeviceIndependentPixels <= 0 ||
            heightInDeviceIndependentPixels <= 0)
        {
            return false;
        }

        uint dpi = screen.Dpi == 0 ? DefaultDpi : screen.Dpi;
        int width = Math.Min(Scale(widthInDeviceIndependentPixels, dpi, DefaultDpi), screen.Width);
        int height = Math.Min(Scale(heightInDeviceIndependentPixels, dpi, DefaultDpi), screen.Height);
        int x = screen.X + ((screen.Width - width) / 2);
        int y = screen.Y + Math.Max(0, (screen.Height - height) / 3);

        return WindowsNativeMethods.SetWindowPosition(
            windowHandle,
            0,
            x,
            y,
            width,
            height,
            WindowsNativeConstants.SetWindowPositionNoActivate);
    }

    public bool TryRestore(nint windowHandle, string placementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementKey);

        StoredWindowPlacement? stored = TryRead(placementKey);
        if (stored is null || !WindowsNativeMethods.IsWindow(windowHandle))
        {
            return false;
        }

        NativeRectangle savedRectangle = new()
        {
            Left = stored.X,
            Top = stored.Y,
            Right = stored.X + stored.Width,
            Bottom = stored.Y + stored.Height,
        };
        nint monitor = WindowsNativeMethods.MonitorFromRectangle(
            in savedRectangle,
            WindowsNativeConstants.MonitorDefaultToNearest);
        if (!TryGetWorkArea(monitor, out NativeRectangle workArea))
        {
            return false;
        }

        uint currentDpi = WindowsNativeMethods.GetDpiForWindow(windowHandle);
        currentDpi = currentDpi == 0 ? DefaultDpi : currentDpi;
        uint savedDpi = stored.Dpi == 0 ? DefaultDpi : stored.Dpi;
        int width = Math.Clamp(
            Scale(stored.Width, currentDpi, savedDpi),
            320,
            workArea.Right - workArea.Left);
        int height = Math.Clamp(
            Scale(stored.Height, currentDpi, savedDpi),
            240,
            workArea.Bottom - workArea.Top);
        int x = Math.Clamp(stored.X, workArea.Left, workArea.Right - width);
        int y = Math.Clamp(stored.Y, workArea.Top, workArea.Bottom - height);

        bool positioned = WindowsNativeMethods.SetWindowPosition(
            windowHandle,
            0,
            x,
            y,
            width,
            height,
            WindowsNativeConstants.SetWindowPositionNoActivate);
        if (positioned && stored.Maximized)
        {
            WindowsNativeMethods.ShowWindow(windowHandle, WindowsNativeConstants.ShowWindowMaximized);
        }

        return positioned;
    }

    public void Save(nint windowHandle, string placementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placementKey);

        if (!WindowsNativeMethods.IsWindow(windowHandle) ||
            !WindowsNativeMethods.GetWindowRectangle(windowHandle, out NativeRectangle rectangle))
        {
            return;
        }

        StoredWindowPlacement placement = new(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right - rectangle.Left,
            rectangle.Bottom - rectangle.Top,
            WindowsNativeMethods.GetDpiForWindow(windowHandle),
            WindowsNativeMethods.IsZoomed(windowHandle));
        try
        {
            _registry.SetString(SettingsSubKey, placementKey, placement.Serialize());
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            // 窗口关闭路径不能被注册表权限或配置损坏阻塞；本次只放弃位置持久化。
        }
    }

    public bool TryActivate(nint windowHandle)
    {
        if (!WindowsNativeMethods.IsWindow(windowHandle))
        {
            return false;
        }

        if (WindowsNativeMethods.IsIconic(windowHandle))
        {
            WindowsNativeMethods.ShowWindow(windowHandle, WindowsNativeConstants.ShowWindowRestore);
        }

        return WindowsNativeMethods.SetForegroundWindow(windowHandle);
    }

    private StoredWindowPlacement? TryRead(string placementKey)
    {
        try
        {
            return StoredWindowPlacement.TryParse(
                _registry.GetString(SettingsSubKey, placementKey),
                out StoredWindowPlacement placement)
                ? placement
                : null;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return null;
        }
    }

    private static bool TryGetWorkArea(nint monitor, out NativeRectangle workArea)
    {
        NativeMonitorInfo info = new()
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMonitorInfo>(),
        };
        if (monitor == 0 || !WindowsNativeMethods.GetMonitorInfo(monitor, ref info))
        {
            workArea = default;
            return false;
        }

        workArea = info.WorkArea;
        return workArea.Right > workArea.Left && workArea.Bottom > workArea.Top;
    }

    private static int Scale(int value, uint targetDpi, uint sourceDpi) =>
        checked((int)Math.Round(
            value * (double)targetDpi / sourceDpi,
            MidpointRounding.AwayFromZero));

    private static bool IsRegistryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private sealed record StoredWindowPlacement(
        int X,
        int Y,
        int Width,
        int Height,
        uint Dpi,
        bool Maximized)
    {
        public string Serialize() => string.Create(
            CultureInfo.InvariantCulture,
            $"{X},{Y},{Width},{Height},{Dpi},{(Maximized ? 1 : 0)}");

        public static bool TryParse(string? value, out StoredWindowPlacement placement)
        {
            placement = new StoredWindowPlacement(0, 0, 0, 0, DefaultDpi, false);
            string[] parts = value?.Split(',', StringSplitOptions.TrimEntries) ?? [];
            if (parts.Length != 6 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) ||
                !uint.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint dpi) ||
                !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximized) ||
                width <= 0 ||
                height <= 0 ||
                maximized is < 0 or > 1)
            {
                return false;
            }

            placement = new StoredWindowPlacement(x, y, width, height, dpi, maximized == 1);
            return true;
        }
    }
}
