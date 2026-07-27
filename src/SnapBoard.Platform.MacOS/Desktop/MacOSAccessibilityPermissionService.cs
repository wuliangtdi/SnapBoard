using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOSAccessibilityPermissionService(
    IPlatformMainThreadDispatcher dispatcher) : IAccessibilityPermissionService
{
    private const string StableBundleIdentifier = "com.wuliangtdi.snapboard";

    public AccessibilityPermissionState GetState() =>
        dispatcher.Invoke(GetStateOnMainThread);

    public AccessibilityPermissionActionResult RequestAccess() => dispatcher.Invoke(() =>
    {
        using NativeAutoreleasePool pool = new();
        nint numberClass = ObjectiveC.GetRequiredClass("NSNumber");
        nint dictionaryClass = ObjectiveC.GetRequiredClass("NSDictionary");
        nint promptKey = ObjectiveC.CreateString("AXTrustedCheckOptionPrompt");
        try
        {
            nint yes = MacOSNativeMethods.SendIntPtrWithByte(
                numberClass,
                ObjectiveC.GetSelector("numberWithBool:"),
                1);
            nint options = MacOSNativeMethods.SendIntPtrWithIntPtrIntPtr(
                dictionaryClass,
                ObjectiveC.GetSelector("dictionaryWithObject:forKey:"),
                yes,
                promptKey);

            // 只有设置页的显式用户命令会进入此路径；普通状态刷新永不触发 TCC 提示。
            MacOSNativeMethods.AXIsProcessTrustedWithOptions(options);
            MacOSNativeMethods.CGRequestPostEventAccess();
            return new AccessibilityPermissionActionResult(
                GetStateOnMainThread(),
                ActionSucceeded: true);
        }
        finally
        {
            ObjectiveC.Release(promptKey);
        }
    });

    public bool OpenSystemSettings() => dispatcher.Invoke(() =>
    {
        using NativeAutoreleasePool pool = new();
        nint urlText = ObjectiveC.CreateString(
            "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
        try
        {
            nint url = MacOSNativeMethods.SendIntPtrWithIntPtr(
                ObjectiveC.GetRequiredClass("NSURL"),
                ObjectiveC.GetSelector("URLWithString:"),
                urlText);
            nint workspace = MacOSNativeMethods.SendIntPtr(
                ObjectiveC.GetRequiredClass("NSWorkspace"),
                ObjectiveC.GetSelector("sharedWorkspace"));
            return url != 0 && workspace != 0 &&
                MacOSNativeMethods.SendBoolWithIntPtr(
                    workspace,
                    ObjectiveC.GetSelector("openURL:"),
                    url) != 0;
        }
        finally
        {
            ObjectiveC.Release(urlText);
        }
    });

    private static AccessibilityPermissionState GetStateOnMainThread()
    {
        bool accessibilityTrusted = MacOSNativeMethods.AXIsProcessTrusted() != 0;
        bool eventPostingAllowed = MacOSNativeMethods.CGPreflightPostEventAccess();
        (ApplicationIdentityKind identityKind, string? bundleIdentifier) = GetIdentity();
        return new AccessibilityPermissionState(
            accessibilityTrusted && eventPostingAllowed
                ? AccessibilityPermissionAccess.Granted
                : AccessibilityPermissionAccess.Denied,
            accessibilityTrusted,
            eventPostingAllowed,
            identityKind,
            bundleIdentifier);
    }

    private static (ApplicationIdentityKind Kind, string? BundleIdentifier) GetIdentity()
    {
        using NativeAutoreleasePool pool = new();
        nint bundle = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSBundle"),
            ObjectiveC.GetSelector("mainBundle"));
        string? bundleIdentifier = ObjectiveC.ToManagedString(MacOSNativeMethods.SendIntPtr(
            bundle,
            ObjectiveC.GetSelector("bundleIdentifier")));
        string? bundlePath = ObjectiveC.ToManagedString(MacOSNativeMethods.SendIntPtr(
            bundle,
            ObjectiveC.GetSelector("bundlePath")));

        bool isStableBundle = bundlePath?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(bundleIdentifier, StableBundleIdentifier, StringComparison.Ordinal);
        return isStableBundle
            ? (ApplicationIdentityKind.AppBundle, bundleIdentifier)
            : (ApplicationIdentityKind.DevelopmentExecutable, bundleIdentifier);
    }
}
