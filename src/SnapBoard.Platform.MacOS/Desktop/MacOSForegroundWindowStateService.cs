using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

internal enum MacOSAccessibilityWindowStatus
{
    Available = 0,
    NoWindow = 1,
    Minimized = 2,
    Failed = 3,
}

internal readonly record struct MacOSWindowBounds(
    double X,
    double Y,
    double Width,
    double Height)
{
    public bool IsValid =>
        double.IsFinite(X) &&
        double.IsFinite(Y) &&
        double.IsFinite(Width) &&
        double.IsFinite(Height) &&
        Width > 0 &&
        Height > 0;
}

internal readonly record struct MacOSAccessibilityWindow(
    MacOSAccessibilityWindowStatus Status,
    MacOSWindowBounds Bounds,
    bool? IsNativeFullScreen = null,
    bool? IsZoomed = null);

internal readonly record struct MacOSWindowMetadata(
    uint WindowId,
    uint ProcessId,
    int Layer,
    bool IsOnScreen,
    MacOSWindowBounds Bounds);

internal readonly record struct MacOSScreenMetadata(
    MacOSWindowBounds Frame,
    MacOSWindowBounds VisibleFrame,
    double BackingScaleFactor);

internal interface IMacOSForegroundWindowNative
{
    uint? GetFrontmostProcessId();

    bool IsAccessibilityTrusted();

    MacOSAccessibilityWindow GetAccessibilityWindow(uint processId);

    IReadOnlyList<MacOSWindowMetadata> GetOnScreenWindows();

    IReadOnlyList<MacOSScreenMetadata> GetScreens();
}

internal sealed class MacOSForegroundWindowNative : IMacOSForegroundWindowNative
{
    private const int AxErrorSuccess = 0;
    private const int AxErrorAttributeUnsupported = -25205;
    private const int AxErrorNoValue = -25212;
    private const uint AxValuePointType = 1;
    private const uint AxValueSizeType = 2;
    private const uint WindowListOptions = (1u << 0) | (1u << 4);
    private const double AccessibilityTimeoutSeconds = 0.25d;

    public uint? GetFrontmostProcessId()
    {
        MacOSAppKit.EnsureInitialized();
        using NativeAutoreleasePool pool = new();
        nint workspace = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSWorkspace"),
            ObjectiveC.GetSelector("sharedWorkspace"));
        nint application = workspace == 0
            ? 0
            : MacOSNativeMethods.SendIntPtr(
                workspace,
                ObjectiveC.GetSelector("frontmostApplication"));
        int processId = application == 0
            ? 0
            : MacOSNativeMethods.SendInt32(
                application,
                ObjectiveC.GetSelector("processIdentifier"));
        return processId > 0 ? checked((uint)processId) : null;
    }

    public bool IsAccessibilityTrusted() => MacOSNativeMethods.AXIsProcessTrusted() != 0;

    public MacOSAccessibilityWindow GetAccessibilityWindow(uint processId)
    {
        nint application = MacOSNativeMethods.AXUIElementCreateApplication(
            checked((int)processId));
        if (application == 0)
        {
            return new MacOSAccessibilityWindow(
                MacOSAccessibilityWindowStatus.Failed,
                default);
        }

        try
        {
            if (MacOSNativeMethods.AXUIElementSetMessagingTimeout(
                    application,
                    (float)AccessibilityTimeoutSeconds) != AxErrorSuccess)
            {
                return new MacOSAccessibilityWindow(
                    MacOSAccessibilityWindowStatus.Failed,
                    default);
            }
            int windowStatus = CopyAttribute(
                application,
                "AXFocusedWindow",
                out nint window);
            if (windowStatus != AxErrorSuccess)
            {
                windowStatus = CopyAttribute(application, "AXMainWindow", out window);
            }

            if (windowStatus is AxErrorNoValue or AxErrorAttributeUnsupported || window == 0)
            {
                Release(window);
                return new MacOSAccessibilityWindow(
                    MacOSAccessibilityWindowStatus.NoWindow,
                    default);
            }

            if (windowStatus != AxErrorSuccess)
            {
                Release(window);
                return new MacOSAccessibilityWindow(
                    MacOSAccessibilityWindowStatus.Failed,
                    default);
            }

            try
            {
                if (TryReadBoolean(window, "AXMinimized", out bool minimized) && minimized)
                {
                    return new MacOSAccessibilityWindow(
                        MacOSAccessibilityWindowStatus.Minimized,
                        default);
                }

                if (!TryReadPoint(window, "AXPosition", out NativePoint position) ||
                    !TryReadSize(window, "AXSize", out NativeSize size))
                {
                    return new MacOSAccessibilityWindow(
                        MacOSAccessibilityWindowStatus.Failed,
                        default);
                }

                MacOSWindowBounds bounds = new(
                    position.X,
                    position.Y,
                    size.Width,
                    size.Height);
                if (!bounds.IsValid)
                {
                    return new MacOSAccessibilityWindow(
                        MacOSAccessibilityWindowStatus.Failed,
                        default);
                }

                bool? isNativeFullScreen = TryReadBoolean(
                    window,
                    "AXFullScreen",
                    out bool fullScreen)
                    ? fullScreen
                    : null;
                bool? isZoomed = TryReadBoolean(window, "AXZoomed", out bool zoomed)
                    ? zoomed
                    : null;
                return new MacOSAccessibilityWindow(
                    MacOSAccessibilityWindowStatus.Available,
                    bounds,
                    isNativeFullScreen,
                    isZoomed);
            }
            finally
            {
                Release(window);
            }
        }
        finally
        {
            Release(application);
        }
    }

    public IReadOnlyList<MacOSWindowMetadata> GetOnScreenWindows()
    {
        nint windows = MacOSNativeMethods.CGWindowListCopyWindowInfo(
            WindowListOptions,
            0);
        if (windows == 0)
        {
            throw new InvalidOperationException("CoreGraphics window metadata is unavailable.");
        }

        using NativeAutoreleasePool pool = new();
        nint windowNumberKey = ObjectiveC.CreateString("kCGWindowNumber");
        nint ownerProcessIdKey = ObjectiveC.CreateString("kCGWindowOwnerPID");
        nint layerKey = ObjectiveC.CreateString("kCGWindowLayer");
        nint onScreenKey = ObjectiveC.CreateString("kCGWindowIsOnscreen");
        nint boundsKey = ObjectiveC.CreateString("kCGWindowBounds");
        try
        {
            List<MacOSWindowMetadata> result = [];
            nuint count = MacOSNativeMethods.SendNUInt(
                windows,
                ObjectiveC.GetSelector("count"));
            for (nuint index = 0; index < count; index++)
            {
                nint dictionary = MacOSNativeMethods.SendIntPtrWithNUInt(
                    windows,
                    ObjectiveC.GetSelector("objectAtIndex:"),
                    index);
                if (!TryReadDictionaryInt(dictionary, windowNumberKey, out int windowId) ||
                    !TryReadDictionaryInt(dictionary, ownerProcessIdKey, out int processId) ||
                    !TryReadDictionaryInt(dictionary, layerKey, out int layer) ||
                    !TryReadDictionaryBoolean(dictionary, onScreenKey, out bool isOnScreen) ||
                    !TryReadDictionaryBounds(dictionary, boundsKey, out MacOSWindowBounds bounds) ||
                    windowId <= 0 || processId <= 0)
                {
                    continue;
                }

                result.Add(new MacOSWindowMetadata(
                    checked((uint)windowId),
                    checked((uint)processId),
                    layer,
                    isOnScreen,
                    bounds));
            }

            return result;
        }
        finally
        {
            ObjectiveC.Release(boundsKey);
            ObjectiveC.Release(onScreenKey);
            ObjectiveC.Release(layerKey);
            ObjectiveC.Release(ownerProcessIdKey);
            ObjectiveC.Release(windowNumberKey);
            MacOSNativeMethods.CFRelease(windows);
        }
    }

    public IReadOnlyList<MacOSScreenMetadata> GetScreens()
    {
        MacOSAppKit.EnsureInitialized();
        using NativeAutoreleasePool pool = new();
        List<RawScreen> rawScreens = [];
        nint screens = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSScreen"),
            ObjectiveC.GetSelector("screens"));
        nuint count = MacOSNativeMethods.SendNUInt(
            screens,
            ObjectiveC.GetSelector("count"));
        for (nuint index = 0; index < count; index++)
        {
            nint screen = MacOSNativeMethods.SendIntPtrWithNUInt(
                screens,
                ObjectiveC.GetSelector("objectAtIndex:"),
                index);
            if (TryReadRectangle(screen, "frame", out MacOSWindowBounds frame) &&
                TryReadRectangle(screen, "visibleFrame", out MacOSWindowBounds visibleFrame))
            {
                double scale = MacOSNativeMethods.SendDouble(
                    screen,
                    ObjectiveC.GetSelector("backingScaleFactor"));
                rawScreens.Add(new RawScreen(
                    frame,
                    visibleFrame,
                    double.IsFinite(scale) && scale >= 1d ? scale : 1d));
            }
        }

        if (rawScreens.Count == 0)
        {
            return [];
        }

        double mainDisplayTop = rawScreens[0].Frame.Y + rawScreens[0].Frame.Height;
        return rawScreens
            .Select(screen => new MacOSScreenMetadata(
                ToTopLeftCoordinates(screen.Frame, mainDisplayTop),
                ToTopLeftCoordinates(screen.VisibleFrame, mainDisplayTop),
                screen.BackingScaleFactor))
            .ToArray();
    }

    private static int CopyAttribute(nint element, string name, out nint value)
    {
        nint attribute = ObjectiveC.CreateString(name);
        try
        {
            return MacOSNativeMethods.AXUIElementCopyAttributeValue(
                element,
                attribute,
                out value);
        }
        finally
        {
            ObjectiveC.Release(attribute);
        }
    }

    private static bool TryReadBoolean(nint element, string name, out bool result)
    {
        int status = CopyAttribute(element, name, out nint value);
        try
        {
            result = status == AxErrorSuccess &&
                value != 0 &&
                MacOSNativeMethods.SendBool(value, ObjectiveC.GetSelector("boolValue")) != 0;
            return status == AxErrorSuccess && value != 0;
        }
        finally
        {
            Release(value);
        }
    }

    private static bool TryReadPoint(nint element, string name, out NativePoint point)
    {
        point = default;
        int status = CopyAttribute(element, name, out nint value);
        try
        {
            return status == AxErrorSuccess &&
                value != 0 &&
                MacOSNativeMethods.AXValueGetPoint(value, AxValuePointType, out point);
        }
        finally
        {
            Release(value);
        }
    }

    private static bool TryReadSize(nint element, string name, out NativeSize size)
    {
        size = default;
        int status = CopyAttribute(element, name, out nint value);
        try
        {
            return status == AxErrorSuccess &&
                value != 0 &&
                MacOSNativeMethods.AXValueGetSize(value, AxValueSizeType, out size);
        }
        finally
        {
            Release(value);
        }
    }

    private static bool TryReadDictionaryInt(nint dictionary, nint key, out int value)
    {
        nint number = ReadDictionaryValue(dictionary, key);
        value = number == 0
            ? 0
            : MacOSNativeMethods.SendInt32(number, ObjectiveC.GetSelector("intValue"));
        return number != 0;
    }

    private static bool TryReadDictionaryBoolean(
        nint dictionary,
        nint key,
        out bool value)
    {
        nint number = ReadDictionaryValue(dictionary, key);
        value = number != 0 &&
            MacOSNativeMethods.SendBool(number, ObjectiveC.GetSelector("boolValue")) != 0;
        return number != 0;
    }

    private static bool TryReadDictionaryBounds(
        nint dictionary,
        nint key,
        out MacOSWindowBounds bounds)
    {
        nint nativeBounds = ReadDictionaryValue(dictionary, key);
        if (nativeBounds == 0 ||
            !MacOSNativeMethods.CGRectMakeWithDictionaryRepresentation(
                nativeBounds,
                out NativeRectangle rectangle))
        {
            bounds = default;
            return false;
        }

        bounds = new MacOSWindowBounds(
            rectangle.Origin.X,
            rectangle.Origin.Y,
            rectangle.Size.Width,
            rectangle.Size.Height);
        return bounds.IsValid;
    }

    private static nint ReadDictionaryValue(nint dictionary, nint key) =>
        dictionary == 0 || key == 0
            ? 0
            : MacOSNativeMethods.SendIntPtrWithIntPtr(
                dictionary,
                ObjectiveC.GetSelector("objectForKey:"),
                key);

    private static bool TryReadRectangle(
        nint target,
        string selectorName,
        out MacOSWindowBounds bounds)
    {
        if (target == 0)
        {
            bounds = default;
            return false;
        }

        nint selector = ObjectiveC.GetSelector(selectorName);
        NativeRectangle rectangle;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            MacOSNativeMethods.SendNativeRectangleStret(out rectangle, target, selector);
        }
        else
        {
            rectangle = MacOSNativeMethods.SendNativeRectangle(target, selector);
        }

        bounds = new MacOSWindowBounds(
            rectangle.Origin.X,
            rectangle.Origin.Y,
            rectangle.Size.Width,
            rectangle.Size.Height);
        return bounds.IsValid;
    }

    private static MacOSWindowBounds ToTopLeftCoordinates(
        MacOSWindowBounds appKitBounds,
        double mainDisplayTop) => new(
            appKitBounds.X,
            mainDisplayTop - (appKitBounds.Y + appKitBounds.Height),
            appKitBounds.Width,
            appKitBounds.Height);

    private static void Release(nint value)
    {
        if (value != 0)
        {
            MacOSNativeMethods.CFRelease(value);
        }
    }

    private readonly record struct RawScreen(
        MacOSWindowBounds Frame,
        MacOSWindowBounds VisibleFrame,
        double BackingScaleFactor);
}

[SupportedOSPlatform("macos")]
public sealed class MacOSForegroundWindowStateService : IPlatformForegroundWindowStateService
{
    private const double BoundsTolerance = 1d;

    private readonly IPlatformMainThreadDispatcher _dispatcher;
    private readonly IMacOSForegroundWindowNative _native;
    private readonly uint _snapBoardProcessId;

    public MacOSForegroundWindowStateService(IPlatformMainThreadDispatcher dispatcher)
        : this(dispatcher, new MacOSForegroundWindowNative(), (uint)Environment.ProcessId)
    {
    }

    internal MacOSForegroundWindowStateService(
        IPlatformMainThreadDispatcher dispatcher,
        IMacOSForegroundWindowNative native,
        uint snapBoardProcessId)
    {
        _dispatcher = dispatcher;
        _native = native;
        _snapBoardProcessId = snapBoardProcessId;
    }

    public ForegroundWindowStateResult GetForegroundWindowState()
    {
        try
        {
            return _dispatcher.Invoke(GetForegroundWindowStateOnMainThread);
        }
        catch
        {
            return Unknown(ForegroundWindowDiagnosticCode.NativeFailure);
        }
    }

    private ForegroundWindowStateResult GetForegroundWindowStateOnMainThread()
    {
        uint? processId = _native.GetFrontmostProcessId();
        if (processId is null or 0)
        {
            return Unavailable(ForegroundWindowDiagnosticCode.NoForegroundWindow);
        }

        if (processId.Value == _snapBoardProcessId)
        {
            return new ForegroundWindowStateResult(
                ForegroundWindowState.Normal,
                IsSnapBoard: true,
                new ForegroundWindowIdentity(0, processId.Value),
                ForegroundWindowDiagnosticCode.SnapBoardWindow);
        }

        if (!_native.IsAccessibilityTrusted())
        {
            return Unknown(ForegroundWindowDiagnosticCode.AccessibilityPermissionDenied);
        }

        MacOSAccessibilityWindow accessibilityWindow =
            _native.GetAccessibilityWindow(processId.Value);
        if (accessibilityWindow.Status == MacOSAccessibilityWindowStatus.NoWindow)
        {
            return Unavailable(ForegroundWindowDiagnosticCode.InvalidWindow);
        }

        if (accessibilityWindow.Status == MacOSAccessibilityWindowStatus.Minimized)
        {
            return Unavailable(ForegroundWindowDiagnosticCode.MinimizedWindow);
        }

        if (accessibilityWindow.Status != MacOSAccessibilityWindowStatus.Available ||
            !accessibilityWindow.Bounds.IsValid)
        {
            return Unknown(ForegroundWindowDiagnosticCode.BoundsUnavailable);
        }

        MacOSWindowMetadata? window = FindForegroundWindow(
            processId.Value,
            accessibilityWindow.Bounds,
            _native.GetOnScreenWindows());
        if (window is null)
        {
            return Unknown(ForegroundWindowDiagnosticCode.BoundsUnavailable);
        }

        ForegroundWindowIdentity identity = new(
            window.Value.WindowId,
            window.Value.ProcessId);
        if (accessibilityWindow.IsNativeFullScreen == true)
        {
            return Available(ForegroundWindowState.FullScreen, identity);
        }

        MacOSScreenMetadata? screen = FindScreen(
            accessibilityWindow.Bounds,
            _native.GetScreens());
        if (screen is null)
        {
            return Unknown(ForegroundWindowDiagnosticCode.MonitorUnavailable, identity);
        }

        if (Covers(accessibilityWindow.Bounds, screen.Value.Frame))
        {
            return Available(ForegroundWindowState.FullScreen, identity);
        }

        if (accessibilityWindow.IsZoomed == true ||
            (!Covers(screen.Value.VisibleFrame, screen.Value.Frame) &&
                Covers(accessibilityWindow.Bounds, screen.Value.VisibleFrame)))
        {
            return Available(ForegroundWindowState.Maximized, identity);
        }

        return Available(ForegroundWindowState.Normal, identity);
    }

    private static MacOSWindowMetadata? FindForegroundWindow(
        uint processId,
        MacOSWindowBounds accessibilityBounds,
        IReadOnlyList<MacOSWindowMetadata> windows)
    {
        MacOSWindowMetadata? best = null;
        double bestOverlap = 0d;
        foreach (MacOSWindowMetadata candidate in windows)
        {
            if (candidate.ProcessId != processId ||
                candidate.Layer != 0 ||
                !candidate.IsOnScreen ||
                !candidate.Bounds.IsValid)
            {
                continue;
            }

            double overlap = IntersectionArea(accessibilityBounds, candidate.Bounds);
            if (overlap > bestOverlap)
            {
                best = candidate;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    private static MacOSScreenMetadata? FindScreen(
        MacOSWindowBounds window,
        IReadOnlyList<MacOSScreenMetadata> screens)
    {
        MacOSScreenMetadata? best = null;
        double bestOverlap = 0d;
        foreach (MacOSScreenMetadata candidate in screens)
        {
            if (!candidate.Frame.IsValid ||
                !candidate.VisibleFrame.IsValid ||
                !double.IsFinite(candidate.BackingScaleFactor) ||
                candidate.BackingScaleFactor < 1d)
            {
                continue;
            }

            double overlap = IntersectionArea(window, candidate.Frame);
            if (overlap > bestOverlap)
            {
                best = candidate;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    private static double IntersectionArea(MacOSWindowBounds left, MacOSWindowBounds right)
    {
        double width = Math.Max(
            0d,
            Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        double height = Math.Max(
            0d,
            Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return width * height;
    }

    private static bool Covers(MacOSWindowBounds window, MacOSWindowBounds target) =>
        window.IsValid &&
        target.IsValid &&
        IsClose(window.X, target.X) &&
        IsClose(window.Y, target.Y) &&
        IsClose(window.X + window.Width, target.X + target.Width) &&
        IsClose(window.Y + window.Height, target.Y + target.Height);

    private static bool IsClose(double left, double right) =>
        Math.Abs(left - right) <= BoundsTolerance;

    private static ForegroundWindowStateResult Available(
        ForegroundWindowState state,
        ForegroundWindowIdentity identity) => new(
            state,
            IsSnapBoard: false,
            identity,
            ForegroundWindowDiagnosticCode.None);

    private static ForegroundWindowStateResult Unknown(
        ForegroundWindowDiagnosticCode diagnostic,
        ForegroundWindowIdentity? identity = null) => new(
            ForegroundWindowState.Unknown,
            IsSnapBoard: false,
            identity,
            diagnostic);

    private static ForegroundWindowStateResult Unavailable(
        ForegroundWindowDiagnosticCode diagnostic) => new(
            ForegroundWindowState.Unavailable,
            IsSnapBoard: false,
            Identity: null,
            diagnostic);
}
