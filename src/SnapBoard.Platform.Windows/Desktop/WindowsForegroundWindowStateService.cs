using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Desktop;

internal interface IWindowsForegroundWindowNative
{
    nint GetForegroundWindow();

    bool IsWindow(nint windowHandle);

    bool IsWindowVisible(nint windowHandle);

    bool IsIconic(nint windowHandle);

    bool IsZoomed(nint windowHandle);

    bool TryGetWindowStyle(nint windowHandle, out uint style);

    nint GetDesktopWindow();

    nint GetShellWindow();

    bool TryGetProcessId(nint windowHandle, out uint processId);

    bool TryGetCloaked(nint windowHandle, out bool cloaked);

    bool TryGetExtendedFrameBounds(nint windowHandle, out NativeRectangle rectangle);

    bool TryGetWindowBounds(nint windowHandle, out NativeRectangle rectangle);

    bool TryGetMonitorBounds(nint windowHandle, out NativeRectangle rectangle);
}

internal sealed class WindowsForegroundWindowNative : IWindowsForegroundWindowNative
{
    public nint GetForegroundWindow() => WindowsNativeMethods.GetForegroundWindow();

    public bool IsWindow(nint windowHandle) => WindowsNativeMethods.IsWindow(windowHandle);

    public bool IsWindowVisible(nint windowHandle) =>
        WindowsNativeMethods.IsWindowVisible(windowHandle);

    public bool IsIconic(nint windowHandle) => WindowsNativeMethods.IsIconic(windowHandle);

    public bool IsZoomed(nint windowHandle) => WindowsNativeMethods.IsZoomed(windowHandle);

    public bool TryGetWindowStyle(nint windowHandle, out uint style)
    {
        Marshal.SetLastPInvokeError(0);
        nint result = WindowsNativeMethods.GetWindowLongPointer(
            windowHandle,
            WindowsNativeConstants.WindowLongStyle);
        style = unchecked((uint)result.ToInt64());
        return result != 0 || Marshal.GetLastPInvokeError() == 0;
    }

    public nint GetDesktopWindow() => WindowsNativeMethods.GetDesktopWindow();

    public nint GetShellWindow() => WindowsNativeMethods.GetShellWindow();

    public bool TryGetProcessId(nint windowHandle, out uint processId) =>
        WindowsNativeMethods.GetWindowThreadProcessId(windowHandle, out processId) != 0 &&
        processId != 0;

    public bool TryGetCloaked(nint windowHandle, out bool cloaked)
    {
        int result = WindowsNativeMethods.DwmGetWindowAttributeUInt32(
            windowHandle,
            WindowsNativeConstants.DwmWindowAttributeCloaked,
            out uint value,
            sizeof(uint));
        cloaked = result == 0 && value != 0;
        return result == 0;
    }

    public bool TryGetExtendedFrameBounds(
        nint windowHandle,
        out NativeRectangle rectangle) =>
        WindowsNativeMethods.DwmGetWindowAttributeRectangle(
            windowHandle,
            WindowsNativeConstants.DwmWindowAttributeExtendedFrameBounds,
            out rectangle,
            (uint)Unsafe.SizeOf<NativeRectangle>()) == 0;

    public bool TryGetWindowBounds(nint windowHandle, out NativeRectangle rectangle) =>
        WindowsNativeMethods.GetWindowRectangle(windowHandle, out rectangle);

    public bool TryGetMonitorBounds(nint windowHandle, out NativeRectangle rectangle)
    {
        nint monitor = WindowsNativeMethods.MonitorFromWindow(
            windowHandle,
            WindowsNativeConstants.MonitorDefaultToNearest);
        NativeMonitorInfo monitorInfo = new()
        {
            Size = (uint)Unsafe.SizeOf<NativeMonitorInfo>(),
        };
        if (monitor == 0 || !WindowsNativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            rectangle = default;
            return false;
        }

        rectangle = monitorInfo.Monitor;
        return true;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsForegroundWindowStateService : IPlatformForegroundWindowStateService
{
    private const int FullScreenBoundsTolerance = 1;
    private readonly uint _snapBoardProcessId;
    private readonly IWindowsForegroundWindowNative _native;

    public WindowsForegroundWindowStateService()
        : this(new WindowsForegroundWindowNative(), (uint)Environment.ProcessId)
    {
    }

    internal WindowsForegroundWindowStateService(
        IWindowsForegroundWindowNative native,
        uint snapBoardProcessId)
    {
        _native = native;
        _snapBoardProcessId = snapBoardProcessId;
    }

    public ForegroundWindowStateResult GetForegroundWindowState()
    {
        try
        {
            nint windowHandle = _native.GetForegroundWindow();
            if (windowHandle == 0)
            {
                return Unavailable(ForegroundWindowDiagnosticCode.NoForegroundWindow);
            }

            if (!_native.IsWindow(windowHandle))
            {
                return Unavailable(ForegroundWindowDiagnosticCode.InvalidWindow);
            }

            if (!_native.TryGetProcessId(windowHandle, out uint processId))
            {
                return Unknown(ForegroundWindowDiagnosticCode.ProcessUnavailable);
            }

            ForegroundWindowIdentity identity = new(
                unchecked((ulong)(nuint)windowHandle),
                processId);
            if (processId == _snapBoardProcessId)
            {
                return new ForegroundWindowStateResult(
                    ForegroundWindowState.Normal,
                    IsSnapBoard: true,
                    identity,
                    ForegroundWindowDiagnosticCode.SnapBoardWindow);
            }

            if (windowHandle == _native.GetDesktopWindow() ||
                windowHandle == _native.GetShellWindow())
            {
                return Unavailable(ForegroundWindowDiagnosticCode.DesktopWindow, identity);
            }

            if (!_native.IsWindowVisible(windowHandle))
            {
                return Unavailable(ForegroundWindowDiagnosticCode.HiddenWindow, identity);
            }

            if (_native.IsIconic(windowHandle))
            {
                return Unavailable(ForegroundWindowDiagnosticCode.MinimizedWindow, identity);
            }

            if (!_native.TryGetCloaked(windowHandle, out bool cloaked))
            {
                return Unknown(ForegroundWindowDiagnosticCode.NativeFailure, identity);
            }

            if (cloaked)
            {
                return Unavailable(ForegroundWindowDiagnosticCode.CloakedWindow, identity);
            }

            bool isZoomed = _native.IsZoomed(windowHandle);

            if (!_native.TryGetMonitorBounds(windowHandle, out NativeRectangle monitorBounds))
            {
                return isZoomed
                    ? Available(ForegroundWindowState.Maximized, identity)
                    : Unknown(ForegroundWindowDiagnosticCode.MonitorUnavailable, identity);
            }

            if (!_native.TryGetExtendedFrameBounds(windowHandle, out NativeRectangle windowBounds) &&
                !_native.TryGetWindowBounds(windowHandle, out windowBounds))
            {
                return isZoomed
                    ? Available(ForegroundWindowState.Maximized, identity)
                    : Unknown(ForegroundWindowDiagnosticCode.BoundsUnavailable, identity);
            }

            if (!CoversMonitor(windowBounds, monitorBounds))
            {
                return Available(
                    isZoomed ? ForegroundWindowState.Maximized : ForegroundWindowState.Normal,
                    identity);
            }

            if (!isZoomed)
            {
                return Available(ForegroundWindowState.FullScreen, identity);
            }

            if (!_native.TryGetWindowStyle(windowHandle, out uint style))
            {
                return Unknown(ForegroundWindowDiagnosticCode.NativeFailure, identity);
            }

            return Available(
                IsBorderless(style)
                    ? ForegroundWindowState.FullScreen
                    : ForegroundWindowState.Maximized,
                identity);
        }
        catch
        {
            return Unknown(ForegroundWindowDiagnosticCode.NativeFailure);
        }
    }

    private static bool CoversMonitor(NativeRectangle window, NativeRectangle monitor) =>
        HasPositiveArea(window) &&
        HasPositiveArea(monitor) &&
        IsClose(window.Left, monitor.Left) &&
        IsClose(window.Top, monitor.Top) &&
        IsClose(window.Right, monitor.Right) &&
        IsClose(window.Bottom, monitor.Bottom);

    private static bool IsBorderless(uint style) =>
        (style & (WindowsNativeConstants.WindowStyleCaption |
            WindowsNativeConstants.WindowStyleThickFrame)) == 0;

    private static bool HasPositiveArea(NativeRectangle rectangle) =>
        rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top;

    private static bool IsClose(int left, int right) =>
        Math.Abs((long)left - right) <= FullScreenBoundsTolerance;

    private static ForegroundWindowStateResult Available(
        ForegroundWindowState state,
        ForegroundWindowIdentity identity) => new(
            state,
            IsSnapBoard: false,
            identity,
            ForegroundWindowDiagnosticCode.None);

    private static ForegroundWindowStateResult Unknown(
        ForegroundWindowDiagnosticCode diagnosticCode,
        ForegroundWindowIdentity? identity = null) => new(
            ForegroundWindowState.Unknown,
            IsSnapBoard: false,
            identity,
            diagnosticCode);

    private static ForegroundWindowStateResult Unavailable(
        ForegroundWindowDiagnosticCode diagnosticCode,
        ForegroundWindowIdentity? identity = null) => new(
            ForegroundWindowState.Unavailable,
            IsSnapBoard: false,
            identity,
            diagnosticCode);
}
