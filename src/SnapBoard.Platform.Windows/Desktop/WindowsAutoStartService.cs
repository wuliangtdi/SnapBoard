using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Platform.Windows.Desktop;

[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStartService : IAutoStartService
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SnapBoard";
    private readonly string _command;
    private readonly IWindowsRegistryStore _registry;

    public WindowsAutoStartService()
        : this(new WindowsRegistryStore(), Environment.ProcessPath)
    {
    }

    internal WindowsAutoStartService(IWindowsRegistryStore registry, string? executablePath)
    {
        _registry = registry;
        _command = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : $"\"{executablePath}\" --background";
    }

    public AutoStartAvailability Availability => AutoStartAvailability.Available;

    public bool IsEnabled()
    {
        if (_command.Length == 0)
        {
            return false;
        }

        try
        {
            return string.Equals(
                _registry.GetString(RunSubKey, ValueName),
                _command,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return false;
        }
    }

    public AutoStartUpdateResult SetEnabled(bool enabled)
    {
        if (_command.Length == 0)
        {
            return new AutoStartUpdateResult(AutoStartUpdateStatus.Failed);
        }

        try
        {
            if (enabled)
            {
                _registry.SetString(RunSubKey, ValueName, _command);
            }
            else
            {
                _registry.DeleteValue(RunSubKey, ValueName);
            }

            return new AutoStartUpdateResult(AutoStartUpdateStatus.Updated);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return new AutoStartUpdateResult(AutoStartUpdateStatus.Failed, exception.HResult);
        }
    }

    private static bool IsRegistryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
