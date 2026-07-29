using System.Globalization;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Platform.Windows.Desktop;

[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopLocalSettingsService : IDesktopLocalSettingsService
{
    internal const string SettingsSubKey = @"Software\SnapBoard\Desktop";
    internal const string VersionValueName = "ConfigurationVersion";
    internal const string PrimaryHotKeyValueName = "PrimaryHotKey";
    internal const string DoubleHotKeyValueName = "DoubleHotKey";
    internal const string ProtectionScopeValueName = "ForegroundProtectionScope";
    internal const string DisableHotKeysValueName = "DisableHotKeysWhenProtected";
    internal const string PauseCaptureValueName = "PauseClipboardCaptureWhenProtected";
    internal const string CurrentVersion = "2";

    private const int MaximumSerializedGestureLength = 256;
    private const int MaximumDisplayNameLength = 128;
    private const GlobalHotKeyModifiers UserModifiers =
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Windows;
    private const GlobalHotKeyModifiers ValidModifiers =
        UserModifiers | GlobalHotKeyModifiers.NoRepeat;

    private readonly object _gate = new();
    private readonly IWindowsRegistryStore _registry;
    private DesktopLocalSettings _current;

    public WindowsDesktopLocalSettingsService()
        : this(new WindowsRegistryStore())
    {
    }

    internal WindowsDesktopLocalSettingsService(IWindowsRegistryStore registry)
    {
        _registry = registry;
        _current = LoadOrInitialize();
    }

    public event EventHandler<DesktopLocalSettingsChangedEventArgs>? Changed;

    public DesktopLocalSettings Current
    {
        get
        {
            lock (_gate)
            {
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

    internal static bool IsValidGesture(
        GlobalHotKeyGesture gesture,
        bool requireModifier = true)
    {
        GlobalHotKeyModifiers modifiers = gesture.Modifiers;
        return gesture.VirtualKey is > 0 and <= 0xFE &&
            (modifiers & ~ValidModifiers) == 0 &&
            (!requireModifier || (modifiers & UserModifiers) != 0) &&
            modifiers.HasFlag(GlobalHotKeyModifiers.NoRepeat) &&
            gesture.DisplayName.Length is > 0 and <= MaximumDisplayNameLength &&
            !string.IsNullOrWhiteSpace(gesture.DisplayName) &&
            !gesture.DisplayName.Contains('|', StringComparison.Ordinal);
    }

    private DesktopLocalSettings LoadOrInitialize()
    {
        DesktopLocalSettings defaults =
            DesktopLocalSettings.CreateDefaults(GlobalHotKeyGesture.WindowsDefault);
        try
        {
            if (_registry.GetString(SettingsSubKey, VersionValueName) == CurrentVersion &&
                TryParseGesture(
                    _registry.GetString(SettingsSubKey, PrimaryHotKeyValueName),
                    requireModifier: true,
                    out GlobalHotKeyGesture primary) &&
                TryParseOptionalGesture(
                    _registry.GetString(SettingsSubKey, DoubleHotKeyValueName),
                    out GlobalHotKeyGesture? doubleGesture) &&
                TryParseProtectionScope(
                    _registry.GetString(SettingsSubKey, ProtectionScopeValueName),
                    out ForegroundProtectionScope protectionScope) &&
                TryParseBoolean(
                    _registry.GetString(SettingsSubKey, DisableHotKeysValueName),
                    out bool disableHotKeys) &&
                TryParseBoolean(
                    _registry.GetString(SettingsSubKey, PauseCaptureValueName),
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
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
        }

        TryPersist(defaults);
        return defaults;
    }

    private bool TryPersist(DesktopLocalSettings settings)
    {
        try
        {
            // 版本值最后提交；进程或注册表写入中断时，下次启动会拒绝整组半写配置。
            _registry.SetString(SettingsSubKey, VersionValueName, "0");
            _registry.SetString(
                SettingsSubKey,
                PrimaryHotKeyValueName,
                SerializeGesture(settings.PrimaryHotKey));
            _registry.SetString(
                SettingsSubKey,
                DoubleHotKeyValueName,
                settings.DoubleHotKey is GlobalHotKeyGesture doubleGesture
                    ? SerializeGesture(doubleGesture)
                    : string.Empty);
            _registry.SetString(
                SettingsSubKey,
                ProtectionScopeValueName,
                SerializeProtectionScope(settings.ProtectionScope));
            _registry.SetString(
                SettingsSubKey,
                DisableHotKeysValueName,
                SerializeBoolean(settings.DisableGlobalHotKeysWhenProtected));
            _registry.SetString(
                SettingsSubKey,
                PauseCaptureValueName,
                SerializeBoolean(settings.PauseClipboardCaptureWhenProtected));
            _registry.SetString(SettingsSubKey, VersionValueName, CurrentVersion);
            return true;
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return false;
        }
    }

    private static bool IsValid(DesktopLocalSettings settings) =>
        IsValidGesture(settings.PrimaryHotKey, requireModifier: true) &&
        Enum.IsDefined(settings.ProtectionScope) &&
        (settings.DoubleHotKey is null ||
            (IsValidGesture(settings.DoubleHotKey.Value, requireModifier: false) &&
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

        if (TryParseGesture(
                value,
                requireModifier: false,
                out GlobalHotKeyGesture parsed))
        {
            gesture = parsed;
            return true;
        }

        gesture = null;
        return false;
    }

    private static bool TryParseGesture(
        string? value,
        bool requireModifier,
        out GlobalHotKeyGesture gesture)
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
        if (!IsValidGesture(parsed, requireModifier))
        {
            return false;
        }

        gesture = parsed;
        return true;
    }

    private static bool TryParseBoolean(string? value, out bool result)
    {
        if (value == "1")
        {
            result = true;
            return true;
        }

        if (value == "0")
        {
            result = false;
            return true;
        }

        result = false;
        return false;
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

    private static string SerializeBoolean(bool value) => value ? "1" : "0";

    private static string SerializeProtectionScope(ForegroundProtectionScope scope) =>
        ((int)scope).ToString(CultureInfo.InvariantCulture);

    private static bool IsRegistryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
