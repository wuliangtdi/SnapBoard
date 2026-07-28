namespace SnapBoard.Platform.Abstractions.Desktop;

/// <summary>
/// 桌面系统恢复事件的跨平台边界。平台层只报告系统唤醒和网络状态变化，
/// 是否立即同步由桌面组合层决定。
/// </summary>
public interface IDesktopSystemEventService : IDisposable
{
    event EventHandler? SystemResumed;

    event EventHandler? NetworkChanged;

    void Start();
}
