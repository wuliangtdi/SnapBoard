using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Platform.MacOS.Clipboard;

/// <summary>
/// 自动粘贴内部包含激活轮询等待，续体可能在线程池运行；所有 AppKit 查询和激活都在这里
/// 同步切回 UI 主线程，避免平台调用方依赖偶然的 SynchronizationContext。
/// </summary>
internal sealed class MainThreadMacOSPasteNative(
    IMacOSPasteNative native,
    IPlatformMainThreadDispatcher dispatcher) : IMacOSPasteNative
{
    public MacOSAutomaticPasteTarget? CaptureForegroundTarget() =>
        dispatcher.Invoke(native.CaptureForegroundTarget);

    public bool IsTargetAvailable(MacOSAutomaticPasteTarget target) =>
        dispatcher.Invoke(() => native.IsTargetAvailable(target));

    public bool HasAccessibilityPermission() =>
        dispatcher.Invoke(native.HasAccessibilityPermission);

    public bool Activate(MacOSAutomaticPasteTarget target) =>
        dispatcher.Invoke(() => native.Activate(target));

    public int GetFrontmostProcessId() =>
        dispatcher.Invoke(native.GetFrontmostProcessId);

    public bool SendPasteShortcut() =>
        dispatcher.Invoke(native.SendPasteShortcut);
}
