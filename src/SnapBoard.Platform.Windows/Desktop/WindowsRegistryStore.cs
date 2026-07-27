using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SnapBoard.Platform.Windows.Desktop;

internal interface IWindowsRegistryStore
{
    string? GetString(string subKey, string name);

    void SetString(string subKey, string name, string value);

    void DeleteValue(string subKey, string name);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistryStore : IWindowsRegistryStore
{
    public string? GetString(string subKey, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetString(string subKey, string name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string subKey, string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
