namespace SnapBoard.Platform.Abstractions.Desktop;

public enum AutoStartUpdateStatus
{
    Updated = 0,
    Failed = 1,
    Unsupported = 2,
}

public sealed record AutoStartUpdateResult(
    AutoStartUpdateStatus Status,
    int NativeErrorCode = 0);

/// <summary>
/// 开机启动由平台层持久化，Desktop 只读写布尔状态。
/// </summary>
public interface IAutoStartService
{
    bool IsEnabled();

    AutoStartUpdateResult SetEnabled(bool enabled);
}
