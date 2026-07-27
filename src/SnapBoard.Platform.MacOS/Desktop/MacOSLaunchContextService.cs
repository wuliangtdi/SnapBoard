using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOSLaunchContextService(
    IPlatformMainThreadDispatcher dispatcher) : ILaunchContextService
{
    private const uint KeyAELaunchedAsLoginItem = 0x6C676974;

    public bool WasLaunchedAsLoginItem() => dispatcher.Invoke(() =>
    {
        using NativeAutoreleasePool pool = new();
        nint manager = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSAppleEventManager"),
            ObjectiveC.GetSelector("sharedAppleEventManager"));
        nint appleEvent = MacOSNativeMethods.SendIntPtr(
            manager,
            ObjectiveC.GetSelector("currentAppleEvent"));
        if (appleEvent == 0)
        {
            return false;
        }

        nint descriptor = MacOSNativeMethods.SendIntPtrWithUInt32(
            appleEvent,
            ObjectiveC.GetSelector("paramDescriptorForKeyword:"),
            KeyAELaunchedAsLoginItem);
        return descriptor != 0;
    });
}
