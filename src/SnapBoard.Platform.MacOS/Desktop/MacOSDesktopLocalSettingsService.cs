using System.Globalization;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOSDesktopLocalSettingsService : IDesktopLocalSettingsService, IDisposable
{
    internal const string VersionSettingName = "DesktopConfigurationVersion";
    internal const string PrimaryHotKeySettingName = "PrimaryHotKey";
    internal const string DoubleHotKeySettingName = "DoubleHotKey";
    internal const string ProtectionScopeSettingName = "ForegroundProtectionScope";
    internal const string DisableHotKeysSettingName = "DisableHotKeysWhenProtected";
    internal const string PauseCaptureSettingName = "PauseClipboardCaptureWhenProtected";
    internal const string CurrentVersion = "1";

    private const int MaximumSerializedGestureLength = 256;
    private const int MaximumDisplayNameLength = 128;
    private const GlobalHotKeyModifiers UserModifiers =
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Meta;
    private const GlobalHotKeyModifiers ValidModifiers =
        UserModifiers | GlobalHotKeyModifiers.NoRepeat;

    private readonly object _gate = new();
    private readonly IMacOSSettingsStore _store;
    private DesktopLocalSettings _current;
    private int _disposed;

    public MacOSDesktopLocalSettingsService()
        : this(new MacOSSettingsStore())
    {
    }

    internal MacOSDesktopLocalSettingsService(IMacOSSettingsStore store)
    {
        _store = store;
        _current = LoadOrInitialize();
    }

    public event EventHandler<DesktopLocalSettingsChangedEventArgs>? Changed;

    public DesktopLocalSettings Current
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                return _current;
            }
        }
    }

    public DesktopLocalSettingsUpdateResult Update(DesktopLocalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Update(_ => settings);
    }

    public DesktopLocalSettingsUpdateResult Update(
        Func<DesktopLocalSettings, DesktopLocalSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        DesktopLocalSettings settings;
        bool persisted;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            settings = update(_current);
            if (!IsValid(settings))
            {
                throw new ArgumentException("The desktop-local settings are invalid.", nameof(update));
            }

            _current = settings;
            persisted = TryPersist(settings);
        }

        Changed?.Invoke(this, new DesktopLocalSettingsChangedEventArgs(settings));
        return new DesktopLocalSettingsUpdateResult(persisted);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _store.Dispose();
            }
        }
    }

    internal static bool IsValidGesture(GlobalHotKeyGesture gesture)
    {
        GlobalHotKeyModifiers requiredMainKeyModifier =
            MacOSHotKeyKeyMap.GetRequiredMainKeyModifier(gesture.VirtualKey);
        return gesture.VirtualKey <= 0x7F &&
            (gesture.Modifiers & ~ValidModifiers) == 0 &&
            gesture.Modifiers.HasFlag(GlobalHotKeyModifiers.NoRepeat) &&
            (requiredMainKeyModifier == GlobalHotKeyModifiers.None ||
                gesture.Modifiers.HasFlag(requiredMainKeyModifier)) &&
            gesture.DisplayName.Length is > 0 and <= MaximumDisplayNameLength &&
            !string.IsNullOrWhiteSpace(gesture.DisplayName) &&
            !gesture.DisplayName.Contains('|', StringComparison.Ordinal);
    }

    private DesktopLocalSettings LoadOrInitialize()
    {
        DesktopLocalSettings defaults =
            DesktopLocalSettings.CreateDefaults(GlobalHotKeyGesture.MacOSDefault);
        try
        {
            if (_store.GetString(VersionSettingName) == CurrentVersion &&
                TryParseGesture(
                    _store.GetString(PrimaryHotKeySettingName),
                    out GlobalHotKeyGesture primary) &&
                TryParseOptionalGesture(
                    _store.GetString(DoubleHotKeySettingName),
                    out GlobalHotKeyGesture? doubleGesture) &&
                TryParseProtectionScope(
                    _store.GetString(ProtectionScopeSettingName),
                    out ForegroundProtectionScope protectionScope) &&
                TryParseBoolean(
                    _store.GetString(DisableHotKeysSettingName),
                    out bool disableHotKeys) &&
                TryParseBoolean(
                    _store.GetString(PauseCaptureSettingName),
                    out bool pauseCapture))
            {
                DesktopLocalSettings settings = new(
                    primary,
                    doubleGesture,
                    protectionScope,
                    disableHotKeys,
                    pauseCapture);
                if (IsValid(settings))
                {
                    return settings;
                }
            }
        }
        catch (Exception exception) when (IsSettingsFailure(exception))
        {
        }

        TryPersist(defaults);
        return defaults;
    }

    private bool TryPersist(DesktopLocalSettings settings)
    {
        try
        {
            // 版本键最后提交；偏好写入中断时，下次启动会拒绝整组半写配置。
            _store.SetString(VersionSettingName, "0");
            _store.SetString(
                PrimaryHotKeySettingName,
                SerializeGesture(settings.PrimaryHotKey));
            _store.SetString(
                DoubleHotKeySettingName,
                settings.DoubleHotKey is GlobalHotKeyGesture doubleGesture
                    ? SerializeGesture(doubleGesture)
                    : string.Empty);
            _store.SetString(
                ProtectionScopeSettingName,
                ((int)settings.ProtectionScope).ToString(CultureInfo.InvariantCulture));
            _store.SetString(
                DisableHotKeysSettingName,
                settings.DisableGlobalHotKeysWhenProtected ? "1" : "0");
            _store.SetString(
                PauseCaptureSettingName,
                settings.PauseClipboardCaptureWhenProtected ? "1" : "0");
            _store.SetString(VersionSettingName, CurrentVersion);
            return true;
        }
        catch (Exception exception) when (IsSettingsFailure(exception))
        {
            return false;
        }
    }

    private static bool IsValid(DesktopLocalSettings settings) =>
        IsValidGesture(settings.PrimaryHotKey) &&
        Enum.IsDefined(settings.ProtectionScope) &&
        (settings.DoubleHotKey is null ||
            (IsValidGesture(settings.DoubleHotKey.Value) &&
                !settings.DoubleHotKey.Value.HasSameBinding(settings.PrimaryHotKey)));

    private static bool TryParseOptionalGesture(
        string? value,
        out GlobalHotKeyGesture? gesture)
    {
        if (value == string.Empty)
        {
            gesture = null;
            return true;
        }

        if (TryParseGesture(value, out GlobalHotKeyGesture parsed))
        {
            gesture = parsed;
            return true;
        }

        gesture = null;
        return false;
    }

    private static bool TryParseGesture(string? value, out GlobalHotKeyGesture gesture)
    {
        gesture = default;
        if (value is null || value.Length is 0 or > MaximumSerializedGestureLength)
        {
            return false;
        }

        string[] parts = value.Split('|', 3, StringSplitOptions.None);
        if (parts.Length != 3 ||
            !uint.TryParse(
                parts[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint modifiers) ||
            !uint.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint virtualKey))
        {
            return false;
        }

        GlobalHotKeyGesture parsed = new(
            (GlobalHotKeyModifiers)modifiers,
            virtualKey,
            parts[2]);
        if (!IsValidGesture(parsed))
        {
            return false;
        }

        gesture = parsed;
        return true;
    }

    private static bool TryParseBoolean(string? value, out bool result)
    {
        result = value == "1";
        return value is "0" or "1";
    }

    private static bool TryParseProtectionScope(
        string? value,
        out ForegroundProtectionScope scope)
    {
        if (int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed) &&
            Enum.IsDefined((ForegroundProtectionScope)parsed))
        {
            scope = (ForegroundProtectionScope)parsed;
            return true;
        }

        scope = default;
        return false;
    }

    private static string SerializeGesture(GlobalHotKeyGesture gesture) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(uint)gesture.Modifiers}|{gesture.VirtualKey}|{gesture.DisplayName}");

    private static bool IsSettingsFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Security.SecurityException;
}
