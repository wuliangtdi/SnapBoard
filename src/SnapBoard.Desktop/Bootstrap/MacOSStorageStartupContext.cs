using System.Runtime.Versioning;
using SnapBoard.Platform.MacOS.Storage;

namespace SnapBoard.Desktop.Bootstrap;

internal static class MacOSStorageStartupContext
{
    [SupportedOSPlatform("macos")]
    public static DesktopStorageStartupContext Create(
        string? applicationDataDirectory,
        string? migrationId) => DesktopStorageStartupContext.Create(
            applicationDataDirectory,
            migrationId,
            new MacOSStoragePlatformService());
}
