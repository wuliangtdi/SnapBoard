namespace SnapBoard.Platform.Abstractions;

public enum PlatformSupportLevel
{
    Unsupported = 0,
    Limited = 1,
    Full = 2,
}

/// <summary>
/// 将平台差异显式暴露给应用层，禁止靠异常或运行时猜测决定功能是否可用。
/// </summary>
public sealed record PlatformCapabilities(
    PlatformSupportLevel ClipboardMonitoring,
    PlatformSupportLevel GlobalHotKeys,
    PlatformSupportLevel AutomaticPaste);
