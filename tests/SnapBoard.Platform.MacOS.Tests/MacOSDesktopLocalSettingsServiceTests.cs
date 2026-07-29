using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Desktop;

namespace SnapBoard.Platform.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSDesktopLocalSettingsServiceTests
{
    [Fact]
    public void FreshInstallUsesAndPersistsCurrentDefaultsWithoutReadingLegacyKey()
    {
        FakeMacOSSettingsStore store = new();
        store.Values["GlobalHotKeyV1"] = "legacy-development-value";
        using MacOSDesktopLocalSettingsService service = new(store);

        Assert.Equal(GlobalHotKeyGesture.MacOSDefault, service.Current.PrimaryHotKey);
        Assert.Null(service.Current.DoubleHotKey);
        Assert.Equal(ForegroundProtectionScope.FullScreenOnly, service.Current.ProtectionScope);
        Assert.True(service.Current.DisableGlobalHotKeysWhenProtected);
        Assert.True(service.Current.PauseClipboardCaptureWhenProtected);
        Assert.Equal(
            MacOSDesktopLocalSettingsService.CurrentVersion,
            store.Values[MacOSDesktopLocalSettingsService.VersionSettingName]);
        Assert.DoesNotContain("GlobalHotKeyV1", store.ReadKeys);
    }

    [Fact]
    public void TwoGesturesAndProtectionSettingsSurviveRestart()
    {
        FakeMacOSSettingsStore store = new();
        DesktopLocalSettings expected = new(
            CreateGesture(
                GlobalHotKeyModifiers.Meta | GlobalHotKeyModifiers.NoRepeat,
                0x09,
                "Command+V"),
            CreateGesture(GlobalHotKeyModifiers.NoRepeat, 0x28, "K"),
            ForegroundProtectionScope.FullScreenAndMaximized,
            DisableGlobalHotKeysWhenProtected: false,
            PauseClipboardCaptureWhenProtected: false);
        using (MacOSDesktopLocalSettingsService first = new(store))
        {
            Assert.True(first.Update(expected).Persisted);
        }

        using MacOSDesktopLocalSettingsService restarted = new(store);

        Assert.Equal(expected, restarted.Current);
    }

    [Fact]
    public void ModifierOnlyGesturesRetainRequiredRegistrationFlags()
    {
        FakeMacOSSettingsStore store = new();
        using MacOSDesktopLocalSettingsService service = new(store);
        GlobalHotKeyGesture modifierOnly = CreateGesture(
            GlobalHotKeyModifiers.Control | GlobalHotKeyModifiers.NoRepeat,
            0x3B,
            "Control");
        GlobalHotKeyGesture invalid = modifierOnly with
        {
            Modifiers = GlobalHotKeyModifiers.NoRepeat,
        };

        Assert.True(MacOSDesktopLocalSettingsService.IsValidGesture(modifierOnly));
        Assert.False(MacOSDesktopLocalSettingsService.IsValidGesture(invalid));
        Assert.Throws<ArgumentException>(() => service.Update(
            service.Current with { DoubleHotKey = invalid }));
    }

    [Fact]
    public void PreviousOrInvalidCurrentFormatResetsWholeConfiguration()
    {
        FakeMacOSSettingsStore store = CreateValidStore();
        store.Values[MacOSDesktopLocalSettingsService.VersionSettingName] = "0";
        using MacOSDesktopLocalSettingsService previous = new(store);

        AssertDefaults(previous.Current);

        store = CreateValidStore();
        store.Values[MacOSDesktopLocalSettingsService.PauseCaptureSettingName] = "invalid";
        using MacOSDesktopLocalSettingsService invalid = new(store);

        AssertDefaults(invalid.Current);
    }

    [Fact]
    public void DuplicateBindingWithDifferentDisplayNameResetsWholeConfiguration()
    {
        FakeMacOSSettingsStore store = CreateValidStore();
        string primary = store.Values[MacOSDesktopLocalSettingsService.PrimaryHotKeySettingName];
        string[] parts = primary.Split('|');
        store.Values[MacOSDesktopLocalSettingsService.DoubleHotKeySettingName] =
            $"{parts[0]}|{parts[1]}|different-display";

        using MacOSDesktopLocalSettingsService service = new(store);

        AssertDefaults(service.Current);
    }

    [Fact]
    public void PersistenceFailureKeepsCurrentSessionSettingsAndReturnsFalse()
    {
        FakeMacOSSettingsStore store = CreateValidStore();
        using MacOSDesktopLocalSettingsService service = new(store);
        store.ThrowOnSet = true;
        DesktopLocalSettings expected = service.Current with
        {
            ProtectionScope = ForegroundProtectionScope.FullScreenAndMaximized,
            DisableGlobalHotKeysWhenProtected = false,
        };

        DesktopLocalSettingsUpdateResult result = service.Update(expected);

        Assert.False(result.Persisted);
        Assert.Equal(expected, service.Current);
    }

    private static FakeMacOSSettingsStore CreateValidStore()
    {
        FakeMacOSSettingsStore store = new();
        DesktopLocalSettings defaults =
            DesktopLocalSettings.CreateDefaults(GlobalHotKeyGesture.MacOSDefault);
        store.Values[MacOSDesktopLocalSettingsService.VersionSettingName] =
            MacOSDesktopLocalSettingsService.CurrentVersion;
        store.Values[MacOSDesktopLocalSettingsService.PrimaryHotKeySettingName] =
            Serialize(defaults.PrimaryHotKey);
        store.Values[MacOSDesktopLocalSettingsService.DoubleHotKeySettingName] = string.Empty;
        store.Values[MacOSDesktopLocalSettingsService.ProtectionScopeSettingName] = "0";
        store.Values[MacOSDesktopLocalSettingsService.DisableHotKeysSettingName] = "1";
        store.Values[MacOSDesktopLocalSettingsService.PauseCaptureSettingName] = "1";
        return store;
    }

    private static void AssertDefaults(DesktopLocalSettings settings)
    {
        Assert.Equal(GlobalHotKeyGesture.MacOSDefault, settings.PrimaryHotKey);
        Assert.Null(settings.DoubleHotKey);
        Assert.Equal(ForegroundProtectionScope.FullScreenOnly, settings.ProtectionScope);
        Assert.True(settings.DisableGlobalHotKeysWhenProtected);
        Assert.True(settings.PauseClipboardCaptureWhenProtected);
    }

    private static GlobalHotKeyGesture CreateGesture(
        GlobalHotKeyModifiers modifiers,
        uint virtualKey,
        string displayName) => new(modifiers, virtualKey, displayName);

    private static string Serialize(GlobalHotKeyGesture gesture) =>
        $"{(uint)gesture.Modifiers}|{gesture.VirtualKey}|{gesture.DisplayName}";

    private sealed class FakeMacOSSettingsStore : IMacOSSettingsStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public List<string> ReadKeys { get; } = [];

        public bool ThrowOnSet { get; set; }

        public string? GetString(string key)
        {
            ReadKeys.Add(key);
            return Values.GetValueOrDefault(key);
        }

        public void SetString(string key, string value)
        {
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("simulated NSUserDefaults failure");
            }

            Values[key] = value;
        }

        public void Dispose()
        {
        }
    }
}
