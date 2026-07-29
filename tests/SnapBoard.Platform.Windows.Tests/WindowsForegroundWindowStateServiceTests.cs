using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Desktop;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsForegroundWindowStateServiceTests
{
    [Fact]
    public void NormalWindowReturnsNormal()
    {
        FakeForegroundWindowNative native = new();
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Normal, result.State);
        Assert.False(result.IsProtected);
        Assert.Equal((uint)42, result.Identity?.ProcessId);
    }

    [Fact]
    public void ZoomedWindowReturnsMaximizedBeforeBoundsComparison()
    {
        FakeForegroundWindowNative native = new()
        {
            Zoomed = true,
            MonitorAvailable = false,
        };
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Maximized, result.State);
        Assert.True(result.IsProtected);
    }

    [Fact]
    public void BorderlessWindowCoveringItsMonitorReturnsFullScreen()
    {
        FakeForegroundWindowNative native = new()
        {
            WindowBounds = Rectangle(0, 0, 1920, 1080),
            MonitorBounds = Rectangle(0, 0, 1920, 1080),
        };
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.FullScreen, result.State);
        Assert.True(result.IsProtected);
    }

    [Fact]
    public void NearlyFullScreenManualWindowIsNotMisclassified()
    {
        FakeForegroundWindowNative native = new()
        {
            WindowBounds = Rectangle(2, 0, 1920, 1080),
            MonitorBounds = Rectangle(0, 0, 1920, 1080),
        };
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Normal, result.State);
    }

    [Fact]
    public void UsesForegroundWindowsMonitorOnMultiMonitorDesktop()
    {
        FakeForegroundWindowNative native = new()
        {
            WindowBounds = Rectangle(1920, -200, 4480, 1240),
            MonitorBounds = Rectangle(1920, -200, 4480, 1240),
        };
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.FullScreen, result.State);
    }

    [Fact]
    public void SnapBoardWindowIsExplicitlyExcluded()
    {
        FakeForegroundWindowNative native = new() { ProcessId = 99, Zoomed = true };
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Normal, result.State);
        Assert.True(result.IsSnapBoard);
        Assert.False(result.IsProtected);
        Assert.Equal(ForegroundWindowDiagnosticCode.SnapBoardWindow, result.DiagnosticCode);
    }

    [Theory]
    [InlineData(ForegroundExclusion.Invalid, ForegroundWindowDiagnosticCode.InvalidWindow)]
    [InlineData(ForegroundExclusion.Hidden, ForegroundWindowDiagnosticCode.HiddenWindow)]
    [InlineData(ForegroundExclusion.Minimized, ForegroundWindowDiagnosticCode.MinimizedWindow)]
    [InlineData(ForegroundExclusion.Cloaked, ForegroundWindowDiagnosticCode.CloakedWindow)]
    [InlineData(ForegroundExclusion.Desktop, ForegroundWindowDiagnosticCode.DesktopWindow)]
    public void ExcludedWindowsReturnUnavailable(
        ForegroundExclusion exclusion,
        ForegroundWindowDiagnosticCode expectedDiagnostic)
    {
        FakeForegroundWindowNative native = new();
        switch (exclusion)
        {
            case ForegroundExclusion.Invalid:
                native.WindowExists = false;
                break;
            case ForegroundExclusion.Hidden:
                native.Visible = false;
                break;
            case ForegroundExclusion.Minimized:
                native.Iconic = true;
                break;
            case ForegroundExclusion.Cloaked:
                native.Cloaked = true;
                break;
            case ForegroundExclusion.Desktop:
                native.DesktopWindow = native.ForegroundWindow;
                break;
        }

        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Unavailable, result.State);
        Assert.Equal(expectedDiagnostic, result.DiagnosticCode);
        Assert.False(result.IsProtected);
    }

    [Fact]
    public void MissingMonitorReturnsUnknownAndAllowsActions()
    {
        FakeForegroundWindowNative native = new() { MonitorAvailable = false };
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Unknown, result.State);
        Assert.Equal(ForegroundWindowDiagnosticCode.MonitorUnavailable, result.DiagnosticCode);
        Assert.False(result.IsProtected);
    }

    [Fact]
    public void UnavailableCloakedStateReturnsUnknownAndAllowsActions()
    {
        FakeForegroundWindowNative native = new() { CloakedStateAvailable = false };
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Unknown, result.State);
        Assert.Equal(ForegroundWindowDiagnosticCode.NativeFailure, result.DiagnosticCode);
        Assert.False(result.IsProtected);
    }

    [Fact]
    public void FallsBackToWindowBoundsWhenDwmBoundsAreUnavailable()
    {
        FakeForegroundWindowNative native = new()
        {
            ExtendedBoundsAvailable = false,
            WindowBounds = Rectangle(0, 0, 1920, 1080),
            MonitorBounds = Rectangle(0, 0, 1920, 1080),
        };
        WindowsForegroundWindowStateService service = new(native, snapBoardProcessId: 99);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.FullScreen, result.State);
        Assert.Equal(1, native.WindowBoundsReadCount);
    }

    private static NativeRectangle Rectangle(int left, int top, int right, int bottom) => new()
    {
        Left = left,
        Top = top,
        Right = right,
        Bottom = bottom,
    };

    public enum ForegroundExclusion
    {
        Invalid,
        Hidden,
        Minimized,
        Cloaked,
        Desktop,
    }

    private sealed class FakeForegroundWindowNative : IWindowsForegroundWindowNative
    {
        public nint ForegroundWindow { get; set; } = 100;

        public nint DesktopWindow { get; set; } = 1;

        public nint ShellWindow { get; set; } = 2;

        public uint ProcessId { get; set; } = 42;

        public bool WindowExists { get; set; } = true;

        public bool Visible { get; set; } = true;

        public bool Iconic { get; set; }

        public bool Zoomed { get; set; }

        public bool Cloaked { get; set; }

        public bool ProcessAvailable { get; set; } = true;

        public bool CloakedStateAvailable { get; set; } = true;

        public bool ExtendedBoundsAvailable { get; set; } = true;

        public bool WindowBoundsAvailable { get; set; } = true;

        public bool MonitorAvailable { get; set; } = true;

        public NativeRectangle WindowBounds { get; set; } = Rectangle(100, 100, 900, 700);

        public NativeRectangle MonitorBounds { get; set; } = Rectangle(0, 0, 1920, 1080);

        public int WindowBoundsReadCount { get; private set; }

        public nint GetForegroundWindow() => ForegroundWindow;

        public bool IsWindow(nint windowHandle) => WindowExists;

        public bool IsWindowVisible(nint windowHandle) => Visible;

        public bool IsIconic(nint windowHandle) => Iconic;

        public bool IsZoomed(nint windowHandle) => Zoomed;

        public nint GetDesktopWindow() => DesktopWindow;

        public nint GetShellWindow() => ShellWindow;

        public bool TryGetProcessId(nint windowHandle, out uint processId)
        {
            processId = ProcessId;
            return ProcessAvailable;
        }

        public bool TryGetCloaked(nint windowHandle, out bool cloaked)
        {
            cloaked = Cloaked;
            return CloakedStateAvailable;
        }

        public bool TryGetExtendedFrameBounds(
            nint windowHandle,
            out NativeRectangle rectangle)
        {
            rectangle = WindowBounds;
            return ExtendedBoundsAvailable;
        }

        public bool TryGetWindowBounds(nint windowHandle, out NativeRectangle rectangle)
        {
            WindowBoundsReadCount++;
            rectangle = WindowBounds;
            return WindowBoundsAvailable;
        }

        public bool TryGetMonitorBounds(nint windowHandle, out NativeRectangle rectangle)
        {
            rectangle = MonitorBounds;
            return MonitorAvailable;
        }
    }
}
