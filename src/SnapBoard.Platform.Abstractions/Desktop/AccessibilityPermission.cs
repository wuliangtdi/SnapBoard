namespace SnapBoard.Platform.Abstractions.Desktop;

public enum AccessibilityPermissionAccess
{
    Granted = 0,
    Denied = 1,
    Unsupported = 2,
}

public enum ApplicationIdentityKind
{
    AppBundle = 0,
    DevelopmentExecutable = 1,
    Unknown = 2,
}

public sealed record AccessibilityPermissionState(
    AccessibilityPermissionAccess Access,
    bool AccessibilityTrusted,
    bool EventPostingAllowed,
    ApplicationIdentityKind IdentityKind,
    string? BundleIdentifier)
{
    public bool IsRestrictedMode => Access != AccessibilityPermissionAccess.Granted;
}

public sealed record AccessibilityPermissionActionResult(
    AccessibilityPermissionState State,
    bool ActionSucceeded);

/// <summary>
/// 权限查询永不触发系统提示；请求授权和打开设置只能由明确的用户命令调用。
/// </summary>
public interface IAccessibilityPermissionService
{
    AccessibilityPermissionState GetState();

    AccessibilityPermissionActionResult RequestAccess();

    bool OpenSystemSettings();
}
