namespace SnapBoard.Platform.Abstractions.Desktop;

public enum ForegroundWindowState
{
    Normal = 0,
    Maximized = 1,
    FullScreen = 2,
    Unknown = 3,
    Unavailable = 4,
}

public enum ForegroundWindowDiagnosticCode
{
    None = 0,
    NoForegroundWindow = 1,
    InvalidWindow = 2,
    HiddenWindow = 3,
    MinimizedWindow = 4,
    CloakedWindow = 5,
    DesktopWindow = 6,
    SnapBoardWindow = 7,
    ProcessUnavailable = 8,
    MonitorUnavailable = 9,
    BoundsUnavailable = 10,
    NativeFailure = 11,
    PlatformNotImplemented = 12,
}

public readonly record struct ForegroundWindowIdentity(ulong WindowId, uint ProcessId);

public sealed record ForegroundWindowStateResult(
    ForegroundWindowState State,
    bool IsSnapBoard,
    ForegroundWindowIdentity? Identity,
    ForegroundWindowDiagnosticCode DiagnosticCode)
{
    public bool IsProtected =>
        !IsSnapBoard && State is ForegroundWindowState.Maximized or ForegroundWindowState.FullScreen;
}

public interface IPlatformForegroundWindowStateService
{
    ForegroundWindowStateResult GetForegroundWindowState();
}
