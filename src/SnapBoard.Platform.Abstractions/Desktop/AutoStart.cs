namespace SnapBoard.Platform.Abstractions.Desktop;

public enum AutoStartUpdateStatus
{
    Updated = 0,
    Failed = 1,
    Unsupported = 2,
    UserApprovalRequired = 3,
}

public enum AutoStartAvailability
{
    Available = 0,
    RequiresAppBundle = 1,
    Unsupported = 2,
    RequiresUserApproval = 3,
}

public sealed record AutoStartUpdateResult(
    AutoStartUpdateStatus Status,
    int NativeErrorCode = 0);

/// <summary>
/// 开机启动由平台层持久化，Desktop 只读写布尔状态。
/// </summary>
public interface IAutoStartService
{
    AutoStartAvailability Availability { get; }

    bool IsEnabled();

    AutoStartUpdateResult SetEnabled(bool enabled);
}
