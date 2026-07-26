using SnapBoard.Platform.Abstractions;

namespace SnapBoard.Platform.Windows;

public static class WindowsPlatformAssembly
{
    public const string PlatformId = "windows";

    public static PlatformCapabilities Capabilities { get; } = new(
        PlatformSupportLevel.Full,
        PlatformSupportLevel.Unsupported,
        PlatformSupportLevel.Limited);
}
