namespace SnapBoard.Platform.Abstractions.Desktop;

/// <summary>
/// 桌面常驻菜单的跨平台命令边界。原生菜单对象、图标和回调生命周期由平台层持有。
/// </summary>
public interface IDesktopMenuBarService : IDisposable
{
    event EventHandler? ShowMainWindowRequested;

    event EventHandler? ShowQuickWindowRequested;

    event EventHandler? RecordingPauseToggleRequested;

    event EventHandler? ShowSettingsWindowRequested;

    event EventHandler? ExitRequested;

    void Initialize(bool recordingPaused);

    void SetRecordingPaused(bool paused);

    void SetRecordingState(
        bool manuallyPaused,
        bool foregroundProtected,
        bool internallyPaused);
}
