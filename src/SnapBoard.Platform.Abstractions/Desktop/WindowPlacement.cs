namespace SnapBoard.Platform.Abstractions.Desktop;

public readonly record struct PlatformScreenPlacement(
    int X,
    int Y,
    int Width,
    int Height,
    uint Dpi);

/// <summary>
/// 平台窗口定位边界。Desktop 仅传入 Avalonia 提供的窗口句柄，
/// DPI 换算、屏幕钳制和持久化细节全部留在平台实现中。
/// </summary>
public interface IPlatformWindowPlacementService
{
    PlatformScreenPlacement? CaptureForegroundScreen();

    bool CenterWindow(
        nint windowHandle,
        PlatformScreenPlacement screen,
        int widthInDeviceIndependentPixels,
        int heightInDeviceIndependentPixels);

    bool TryRestore(nint windowHandle, string placementKey);

    void Save(nint windowHandle, string placementKey);

    bool TryActivate(nint windowHandle);
}
