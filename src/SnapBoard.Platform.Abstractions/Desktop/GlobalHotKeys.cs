namespace SnapBoard.Platform.Abstractions.Desktop;

[Flags]
public enum GlobalHotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    Meta = Windows,
    NoRepeat = 0x4000,
}

public readonly record struct GlobalHotKeyGesture(
    GlobalHotKeyModifiers Modifiers,
    uint VirtualKey,
    string DisplayName)
{
    public static GlobalHotKeyGesture WindowsDefault { get; } = new(
        GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.Shift | GlobalHotKeyModifiers.NoRepeat,
        0x56,
        "Ctrl+Shift+V");

    public static GlobalHotKeyGesture MacOSDefault { get; } = new(
        GlobalHotKeyModifiers.Meta | GlobalHotKeyModifiers.Shift | GlobalHotKeyModifiers.NoRepeat,
        0x09,
        "Command+Shift+V");

    public static GlobalHotKeyGesture Default => WindowsDefault;
}

public enum GlobalHotKeyRegistrationStatus
{
    Registered = 0,
    Conflict = 1,
    Failed = 2,
    Unsupported = 3,
}

public enum GlobalHotKeyGestureCreationStatus
{
    Created = 0,
    MissingModifier = 1,
    UnsupportedKey = 2,
}

public sealed record GlobalHotKeyGestureCreationResult(
    GlobalHotKeyGestureCreationStatus Status,
    GlobalHotKeyGesture? Gesture = null);

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

    GlobalHotKeyGesture DefaultGesture { get; }

    string ModifierDisplayNames { get; }

    GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeyModifiers modifiers,
        string keyName);

    ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken);

    ValueTask UnregisterAsync(CancellationToken cancellationToken);
}
