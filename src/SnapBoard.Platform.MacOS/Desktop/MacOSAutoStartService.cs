using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOSAutoStartService : IAutoStartService
{
    private const string ServiceManagementPath =
        "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement";
    private const string StableBundleIdentifier = "com.wuliangtdi.snapboard";
    private const int StatusEnabled = 1;
    private const int StatusRequiresApproval = 2;
    private static readonly object FrameworkGate = new();
    private static nint _frameworkHandle;

    private readonly IPlatformMainThreadDispatcher _dispatcher;
    private readonly bool _isAppBundle;

    public MacOSAutoStartService(IPlatformMainThreadDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _isAppBundle = dispatcher.Invoke(IsStableAppBundle);
    }

    public AutoStartAvailability Availability
    {
        get
        {
            if (!_isAppBundle)
            {
                return AutoStartAvailability.RequiresAppBundle;
            }

            return _dispatcher.Invoke(() => GetStatus() == StatusRequiresApproval
                ? AutoStartAvailability.RequiresUserApproval
                : AutoStartAvailability.Available);
        }
    }

    public bool IsEnabled() =>
        _isAppBundle && _dispatcher.Invoke(() => GetStatus() == StatusEnabled);

    public AutoStartUpdateResult SetEnabled(bool enabled)
    {
        if (!_isAppBundle)
        {
            return new AutoStartUpdateResult(AutoStartUpdateStatus.Unsupported);
        }

        return _dispatcher.Invoke(() => SetEnabledOnMainThread(enabled));
    }

    private static AutoStartUpdateResult SetEnabledOnMainThread(bool enabled)
    {
        using NativeAutoreleasePool pool = new();
        nint service = GetMainAppService();
        if (service == 0)
        {
            return new AutoStartUpdateResult(AutoStartUpdateStatus.Unsupported);
        }

        nint error = 0;
        unsafe
        {
            bool succeeded = MacOSNativeMethods.SendBoolWithIntPtr(
                service,
                ObjectiveC.GetSelector(enabled
                    ? "registerAndReturnError:"
                    : "unregisterAndReturnError:"),
                (nint)(&error)) != 0;
            if (!succeeded)
            {
                int code = error == 0
                    ? -1
                    : MacOSNativeMethods.SendInt32(error, ObjectiveC.GetSelector("code"));
                return new AutoStartUpdateResult(AutoStartUpdateStatus.Failed, code);
            }
        }

        return enabled && GetStatus(service) == StatusRequiresApproval
            ? new AutoStartUpdateResult(AutoStartUpdateStatus.UserApprovalRequired)
            : new AutoStartUpdateResult(AutoStartUpdateStatus.Updated);
    }

    private static int GetStatus()
    {
        using NativeAutoreleasePool pool = new();
        return GetStatus(GetMainAppService());
    }

    private static int GetStatus(nint service) => service == 0
        ? -1
        : MacOSNativeMethods.SendInt32(service, ObjectiveC.GetSelector("status"));

    private static nint GetMainAppService()
    {
        EnsureFrameworkLoaded();
        nint serviceClass = MacOSNativeMethods.GetClass("SMAppService");
        return serviceClass == 0
            ? 0
            : MacOSNativeMethods.SendIntPtr(
                serviceClass,
                ObjectiveC.GetSelector("mainAppService"));
    }

    private static void EnsureFrameworkLoaded()
    {
        if (Volatile.Read(ref _frameworkHandle) != 0)
        {
            return;
        }

        lock (FrameworkGate)
        {
            if (_frameworkHandle == 0 &&
                NativeLibrary.TryLoad(ServiceManagementPath, out nint framework))
            {
                // Objective-C 类的生命周期依赖 framework 保持加载；进程退出时由 dyld 统一卸载。
                Volatile.Write(ref _frameworkHandle, framework);
            }
        }
    }

    private static bool IsStableAppBundle()
    {
        using NativeAutoreleasePool pool = new();
        nint bundle = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSBundle"),
            ObjectiveC.GetSelector("mainBundle"));
        string? identifier = ObjectiveC.ToManagedString(MacOSNativeMethods.SendIntPtr(
            bundle,
            ObjectiveC.GetSelector("bundleIdentifier")));
        string? path = ObjectiveC.ToManagedString(MacOSNativeMethods.SendIntPtr(
            bundle,
            ObjectiveC.GetSelector("bundlePath")));
        return path?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(identifier, StableBundleIdentifier, StringComparison.Ordinal);
    }
}
