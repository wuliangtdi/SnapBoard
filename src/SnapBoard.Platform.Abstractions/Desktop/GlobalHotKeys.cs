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
    public bool HasSameBinding(GlobalHotKeyGesture other) =>
        Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;

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
    Duplicate = 4,
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
    int NativeErrorCode = 0,
    bool SettingsPersisted = true);

#pragma warning disable CA1720 // Double 是已确认的快捷键触发来源协议名，不表示数值类型。
public enum GlobalHotKeySlot
{
    Primary = 0,
    Double = 1,
}
#pragma warning restore CA1720

public sealed class GlobalHotKeyTriggeredEventArgs(
    GlobalHotKeySlot source,
    bool isRepeat = false) : EventArgs
{
    public GlobalHotKeySlot Source { get; } = source;

    public bool IsRepeat { get; } = isRepeat;
}

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

/// <summary>
/// 两槽全局快捷键能力。平台未完成原生双槽实现时不注册该能力，不能用空成功实现代替。
/// </summary>
public interface ITwoSlotGlobalHotKeyService
{
    event EventHandler<GlobalHotKeyTriggeredEventArgs>? Triggered;

    TimeSpan DoubleTriggerInterval { get; }

    GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeySlot slot,
        GlobalHotKeyModifiers modifiers,
        string keyName);

    GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot);

    GlobalHotKeyGesture? GetConfiguredGesture(GlobalHotKeySlot slot);

    ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeySlot slot,
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken);

    ValueTask<GlobalHotKeyRegistrationResult> ClearAsync(
        GlobalHotKeySlot slot,
        CancellationToken cancellationToken);
}
