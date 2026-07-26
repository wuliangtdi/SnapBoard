namespace SnapBoard.Platform.Abstractions.Clipboard;

/// <summary>
/// 平台目标窗口的不透明令牌。Application 只保存并回传令牌，
/// 不读取其中的原生窗口句柄或进程信息。
/// </summary>
public interface IAutomaticPasteTarget;

public enum AutomaticPasteStatus
{
    Pasted = 0,
    ManualPasteRequired = 1,
    TargetUnavailable = 2,
    Unsupported = 3,
}

public enum AutomaticPasteFailureReason
{
    None = 0,
    InvalidTarget = 1,
    HigherIntegrityTarget = 2,
    IntegrityLevelUnavailable = 3,
    TargetActivationFailed = 4,
    InputInjectionBlocked = 5,
    PlatformUnavailable = 6,
}

public sealed record AutomaticPasteResult(
    AutomaticPasteStatus Status,
    AutomaticPasteFailureReason FailureReason = AutomaticPasteFailureReason.None)
{
    public const string ManualPasteRequiredMessage = "已复制，请手动粘贴";
}

/// <summary>
/// 写回剪贴板后恢复目标窗口并尝试注入粘贴快捷键。
/// 权限或 UIPI 阻止输入时必须返回手动粘贴降级结果。
/// </summary>
public interface IAutomaticPasteService
{
    IAutomaticPasteTarget? CaptureForegroundTarget();

    ValueTask<AutomaticPasteResult> TryPasteAsync(
        IAutomaticPasteTarget target,
        CancellationToken cancellationToken);
}
