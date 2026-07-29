using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Desktop;

namespace SnapBoard.Platform.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSForegroundWindowStateServiceTests
{
    private const uint SnapBoardProcessId = 42;
    private const uint ForegroundProcessId = 100;

    [Fact]
    public void NormalWindowReturnsNormalWithStableIdentity()
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(100, 100, 900, 700));
        MacOSForegroundWindowStateService service = CreateService(native);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Normal, result.State);
        Assert.Equal(new ForegroundWindowIdentity(500, ForegroundProcessId), result.Identity);
        Assert.Equal(ForegroundWindowDiagnosticCode.None, result.DiagnosticCode);
    }

    [Fact]
    public void ZoomedWindowReturnsMaximizedButDefaultScopeAllowsIt()
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(120, 80, 1500, 900));
        native.AccessibilityWindow = native.AccessibilityWindow with
        {
            IsZoomed = true,
        };
        MacOSForegroundWindowStateService service = CreateService(native);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Maximized, result.State);
        Assert.False(result.IsProtected(ForegroundProtectionScope.FullScreenOnly));
        Assert.True(result.IsProtected(ForegroundProtectionScope.FullScreenAndMaximized));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeAndBorderlessFullScreenReturnFullScreen(bool nativeFullScreen)
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(0, 0, 1920, 1080));
        native.AccessibilityWindow = native.AccessibilityWindow with
        {
            IsNativeFullScreen = nativeFullScreen,
        };
        MacOSForegroundWindowStateService service = CreateService(native);

        ForegroundWindowStateResult result = service.GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.FullScreen, result.State);
        Assert.True(result.IsProtected(ForegroundProtectionScope.FullScreenOnly));
    }

    [Fact]
    public void NativeFullScreenDoesNotDependOnScreenMetadata()
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(0, 0, 1920, 1080));
        native.AccessibilityWindow = native.AccessibilityWindow with
        {
            IsNativeFullScreen = true,
        };
        native.Screens = [];

        ForegroundWindowStateResult result = CreateService(native).GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.FullScreen, result.State);
    }

    [Fact]
    public void NearScreenSizedWindowIsNotMisclassified()
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(2, 2, 1916, 1076));

        ForegroundWindowStateResult result = CreateService(native).GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Normal, result.State);
    }

    [Fact]
    public void UnrelatedProcessWindowWithNoOverlapIsNotUsedAsIdentity()
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(100, 100, 900, 700));
        native.Windows =
        [
            new MacOSWindowMetadata(
                500,
                ForegroundProcessId,
                Layer: 0,
                IsOnScreen: true,
                new MacOSWindowBounds(3000, 3000, 500, 500)),
        ];

        ForegroundWindowStateResult result = CreateService(native).GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Unknown, result.State);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void UsesForegroundWindowsDisplayAndKeepsRetinaCoordinatesInPoints()
    {
        MacOSWindowBounds secondDisplay = new(-1440, 0, 1440, 900);
        FakeMacOSForegroundWindowNative native = CreateNative(secondDisplay);
        native.Screens =
        [
            new MacOSScreenMetadata(
                new MacOSWindowBounds(0, 0, 1920, 1080),
                new MacOSWindowBounds(0, 25, 1920, 1055),
                1),
            new MacOSScreenMetadata(
                secondDisplay,
                new MacOSWindowBounds(-1440, 23, 1440, 877),
                2),
        ];

        ForegroundWindowStateResult result = CreateService(native).GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.FullScreen, result.State);
        Assert.Equal(new ForegroundWindowIdentity(500, ForegroundProcessId), result.Identity);
    }

    [Fact]
    public void PermissionDeniedReturnsUnknownWithoutReadingWindowMetadata()
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(0, 0, 1920, 1080));
        native.AccessibilityTrusted = false;

        ForegroundWindowStateResult result = CreateService(native).GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Unknown, result.State);
        Assert.Equal(
            ForegroundWindowDiagnosticCode.AccessibilityPermissionDenied,
            result.DiagnosticCode);
        Assert.Equal(0, native.AccessibilityWindowCallCount);
        Assert.Equal(0, native.WindowMetadataCallCount);
    }

    [Fact]
    public void SnapBoardWindowIsExcludedBeforePermissionQuery()
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(0, 0, 1920, 1080));
        native.FrontmostProcessId = SnapBoardProcessId;
        native.AccessibilityTrusted = false;

        ForegroundWindowStateResult result = CreateService(native).GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Normal, result.State);
        Assert.True(result.IsSnapBoard);
        Assert.False(result.IsProtected(ForegroundProtectionScope.FullScreenAndMaximized));
        Assert.Equal(0, native.AccessibilityTrustCallCount);
    }

    [Theory]
    [InlineData((int)MacOSAccessibilityWindowStatus.NoWindow, ForegroundWindowState.Unavailable)]
    [InlineData((int)MacOSAccessibilityWindowStatus.Minimized, ForegroundWindowState.Unavailable)]
    [InlineData((int)MacOSAccessibilityWindowStatus.Failed, ForegroundWindowState.Unknown)]
    public void MissingMinimizedAndFailedWindowsFailOpen(
        int statusValue,
        ForegroundWindowState expected)
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(0, 0, 1920, 1080));
        native.AccessibilityWindow = new MacOSAccessibilityWindow(
            (MacOSAccessibilityWindowStatus)statusValue,
            default);

        ForegroundWindowStateResult result = CreateService(native).GetForegroundWindowState();

        Assert.Equal(expected, result.State);
        Assert.False(result.IsProtected(ForegroundProtectionScope.FullScreenAndMaximized));
    }

    [Fact]
    public void NoForegroundWindowAndNativeFailureDoNotThrow()
    {
        FakeMacOSForegroundWindowNative native = CreateNative(
            new MacOSWindowBounds(0, 0, 1920, 1080));
        native.FrontmostProcessId = null;
        Assert.Equal(
            ForegroundWindowState.Unavailable,
            CreateService(native).GetForegroundWindowState().State);

        native.FrontmostProcessId = ForegroundProcessId;
        native.ThrowOnWindowMetadata = true;
        ForegroundWindowStateResult failed =
            CreateService(native).GetForegroundWindowState();

        Assert.Equal(ForegroundWindowState.Unknown, failed.State);
        Assert.Equal(ForegroundWindowDiagnosticCode.NativeFailure, failed.DiagnosticCode);
    }

    private static MacOSForegroundWindowStateService CreateService(
        IMacOSForegroundWindowNative native) => new(
            DirectPlatformMainThreadDispatcher.Instance,
            native,
            SnapBoardProcessId);

    private static FakeMacOSForegroundWindowNative CreateNative(MacOSWindowBounds bounds) => new()
    {
        FrontmostProcessId = ForegroundProcessId,
        AccessibilityWindow = new MacOSAccessibilityWindow(
            MacOSAccessibilityWindowStatus.Available,
            bounds),
        Windows =
        [
            new MacOSWindowMetadata(
                500,
                ForegroundProcessId,
                Layer: 0,
                IsOnScreen: true,
                bounds),
        ],
        Screens =
        [
            new MacOSScreenMetadata(
                new MacOSWindowBounds(0, 0, 1920, 1080),
                new MacOSWindowBounds(0, 25, 1920, 1055),
                2),
        ],
    };

    private sealed class FakeMacOSForegroundWindowNative : IMacOSForegroundWindowNative
    {
        public uint? FrontmostProcessId { get; set; }

        public bool AccessibilityTrusted { get; set; } = true;

        public MacOSAccessibilityWindow AccessibilityWindow { get; set; }

        public IReadOnlyList<MacOSWindowMetadata> Windows { get; set; } = [];

        public IReadOnlyList<MacOSScreenMetadata> Screens { get; set; } = [];

        public bool ThrowOnWindowMetadata { get; set; }

        public int AccessibilityTrustCallCount { get; private set; }

        public int AccessibilityWindowCallCount { get; private set; }

        public int WindowMetadataCallCount { get; private set; }

        public uint? GetFrontmostProcessId() => FrontmostProcessId;

        public bool IsAccessibilityTrusted()
        {
            AccessibilityTrustCallCount++;
            return AccessibilityTrusted;
        }

        public MacOSAccessibilityWindow GetAccessibilityWindow(uint processId)
        {
            AccessibilityWindowCallCount++;
            return AccessibilityWindow;
        }

        public IReadOnlyList<MacOSWindowMetadata> GetOnScreenWindows()
        {
            WindowMetadataCallCount++;
            return ThrowOnWindowMetadata
                ? throw new InvalidOperationException("simulated native failure")
                : Windows;
        }

        public IReadOnlyList<MacOSScreenMetadata> GetScreens() => Screens;
    }
}
