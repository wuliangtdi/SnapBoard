namespace SnapBoard.Platform.Abstractions.Desktop;

[Flags]
public enum GlobalHotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000,
}

public readonly record struct GlobalHotKeyGesture(
    GlobalHotKeyModifiers Modifiers,
    uint VirtualKey,
    string DisplayName)
{
    public static GlobalHotKeyGesture Default { get; } = new(
        GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Shift | GlobalHotKeyModifiers.NoRepeat,
        0x56,
        "Ctrl+Shift+V");
}

public enum GlobalHotKeyRegistrationStatus
{
    Registered = 0,
    Conflict = 1,
    Failed = 2,
    Unsupported = 3,
}

public sealed record GlobalHotKeyRegistrationResult(
    GlobalHotKeyRegistrationStatus Status,
    int NativeErrorCode = 0);

/// <summary>
/// 全局快捷键的跨平台边界。原生消息窗口、注册 ID 和 Win32 错误码均由平台层管理。
/// </summary>
public interface IGlobalHotKeyService : IAsyncDisposable
{
    event EventHandler? Pressed;

    GlobalHotKeyGesture? CurrentGesture { get; }

    GlobalHotKeyGesture ConfiguredGesture { get; }

    ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken);

    ValueTask UnregisterAsync(CancellationToken cancellationToken);
}
