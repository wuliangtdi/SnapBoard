namespace SnapBoard.Platform.Abstractions.Desktop;

public sealed record DesktopLocalSettings(
    GlobalHotKeyGesture PrimaryHotKey,
    GlobalHotKeyGesture? DoubleHotKey,
    bool DisableGlobalHotKeysWhenProtected,
    bool PauseClipboardCaptureWhenProtected)
{
    public static DesktopLocalSettings CreateDefaults(GlobalHotKeyGesture primaryHotKey) => new(
        primaryHotKey,
        DoubleHotKey: null,
        DisableGlobalHotKeysWhenProtected: true,
        PauseClipboardCaptureWhenProtected: true);
}

public sealed class DesktopLocalSettingsChangedEventArgs(
    DesktopLocalSettings settings) : EventArgs
{
    public DesktopLocalSettings Settings { get; } = settings;
}

public sealed record DesktopLocalSettingsUpdateResult(bool Persisted);

/// <summary>
/// 设备本机设置边界。实现只能写平台用户设置，不得写入同步数据库或生成 Outbox 事件。
/// </summary>
public interface IDesktopLocalSettingsService
{
    event EventHandler<DesktopLocalSettingsChangedEventArgs>? Changed;

    DesktopLocalSettings Current { get; }

    DesktopLocalSettingsUpdateResult Update(DesktopLocalSettings settings);

    DesktopLocalSettingsUpdateResult Update(
        Func<DesktopLocalSettings, DesktopLocalSettings> update);
}
