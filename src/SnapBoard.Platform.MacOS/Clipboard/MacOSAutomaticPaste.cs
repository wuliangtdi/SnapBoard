using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Clipboard;

internal interface IMacOSPasteNative
{
    MacOSAutomaticPasteTarget? CaptureForegroundTarget();

    bool IsTargetAvailable(MacOSAutomaticPasteTarget target);

    bool HasAccessibilityPermission();

    bool Activate(MacOSAutomaticPasteTarget target);

    int GetFrontmostProcessId();

    bool SendPasteShortcut();
}

internal sealed record MacOSAutomaticPasteTarget(
    int ProcessId,
    string? BundleIdentifier,
    string? LocalizedName) : IAutomaticPasteTarget;

internal sealed class MacOSPasteNative : IMacOSPasteNative
{
    private const nuint ActivateAllWindows = 1;
    private const ulong CommandFlag = 0x00100000;
    private const int HidEventTap = 0;
    private const ushort VirtualKeyV = 9;

    private readonly nint _runningApplicationClass;
    private readonly nint _workspaceClass;
    private readonly nint _activateWithOptionsSelector;
    private readonly nint _bundleIdentifierSelector;
    private readonly nint _frontmostApplicationSelector;
    private readonly nint _localizedNameSelector;
    private readonly nint _processIdentifierSelector;
    private readonly nint _runningApplicationWithProcessIdentifierSelector;
    private readonly nint _sharedWorkspaceSelector;

    public MacOSPasteNative()
    {
        MacOSAppKit.EnsureInitialized();

        _runningApplicationClass = ObjectiveC.GetRequiredClass("NSRunningApplication");
        _workspaceClass = ObjectiveC.GetRequiredClass("NSWorkspace");
        _activateWithOptionsSelector = ObjectiveC.GetSelector("activateWithOptions:");
        _bundleIdentifierSelector = ObjectiveC.GetSelector("bundleIdentifier");
        _frontmostApplicationSelector = ObjectiveC.GetSelector("frontmostApplication");
        _localizedNameSelector = ObjectiveC.GetSelector("localizedName");
        _processIdentifierSelector = ObjectiveC.GetSelector("processIdentifier");
        _runningApplicationWithProcessIdentifierSelector =
            ObjectiveC.GetSelector("runningApplicationWithProcessIdentifier:");
        _sharedWorkspaceSelector = ObjectiveC.GetSelector("sharedWorkspace");
    }

    public MacOSAutomaticPasteTarget? CaptureForegroundTarget()
    {
        using NativeAutoreleasePool pool = new();
        nint application = GetFrontmostApplication();
        if (application == 0)
        {
            return null;
        }

        int processId = MacOSNativeMethods.SendInt32(application, _processIdentifierSelector);
        if (processId <= 0)
        {
            return null;
        }

        return new MacOSAutomaticPasteTarget(
            processId,
            ObjectiveC.ToManagedString(MacOSNativeMethods.SendIntPtr(
                application,
                _bundleIdentifierSelector)),
            ObjectiveC.ToManagedString(MacOSNativeMethods.SendIntPtr(
                application,
                _localizedNameSelector)));
    }

    public bool IsTargetAvailable(MacOSAutomaticPasteTarget target)
    {
        using NativeAutoreleasePool pool = new();
        nint application = GetRunningApplication(target.ProcessId);
        if (application == 0)
        {
            return false;
        }

        string? bundleIdentifier = ObjectiveC.ToManagedString(
            MacOSNativeMethods.SendIntPtr(application, _bundleIdentifierSelector));
        return target.BundleIdentifier is null ||
            string.Equals(
                target.BundleIdentifier,
                bundleIdentifier,
                StringComparison.Ordinal);
    }

    public bool HasAccessibilityPermission() =>
        MacOSNativeMethods.AXIsProcessTrusted() != 0 &&
        MacOSNativeMethods.CGPreflightPostEventAccess();

    public bool Activate(MacOSAutomaticPasteTarget target)
    {
        using NativeAutoreleasePool pool = new();
        nint application = GetRunningApplication(target.ProcessId);
        return application != 0 &&
            MacOSNativeMethods.SendBoolWithNUInt(
                application,
                _activateWithOptionsSelector,
                ActivateAllWindows) != 0;
    }

    public int GetFrontmostProcessId()
    {
        using NativeAutoreleasePool pool = new();
        nint application = GetFrontmostApplication();
        return application == 0
            ? 0
            : MacOSNativeMethods.SendInt32(application, _processIdentifierSelector);
    }

    public bool SendPasteShortcut()
    {
        nint keyDown = MacOSNativeMethods.CGEventCreateKeyboardEvent(0, VirtualKeyV, true);
        nint keyUp = MacOSNativeMethods.CGEventCreateKeyboardEvent(0, VirtualKeyV, false);
        if (keyDown == 0 || keyUp == 0)
        {
            ReleaseEvent(keyDown);
            ReleaseEvent(keyUp);
            return false;
        }

        try
        {
            MacOSNativeMethods.CGEventSetFlags(keyDown, CommandFlag);
            MacOSNativeMethods.CGEventSetFlags(keyUp, CommandFlag);
            MacOSNativeMethods.CGEventPost(HidEventTap, keyDown);
            MacOSNativeMethods.CGEventPost(HidEventTap, keyUp);
            return true;
        }
        finally
        {
            // CGEventCreate* 遵循 Create Rule；每个事件都必须显式 CFRelease。
            MacOSNativeMethods.CFRelease(keyDown);
            MacOSNativeMethods.CFRelease(keyUp);
        }
    }

    private nint GetFrontmostApplication()
    {
        nint workspace = MacOSNativeMethods.SendIntPtr(_workspaceClass, _sharedWorkspaceSelector);
        return workspace == 0
            ? 0
            : MacOSNativeMethods.SendIntPtr(workspace, _frontmostApplicationSelector);
    }

    private nint GetRunningApplication(int processId) =>
        MacOSNativeMethods.SendIntPtrWithInt32(
            _runningApplicationClass,
            _runningApplicationWithProcessIdentifierSelector,
            processId);

    private static void ReleaseEvent(nint keyboardEvent)
    {
        if (keyboardEvent != 0)
        {
            MacOSNativeMethods.CFRelease(keyboardEvent);
        }
    }
}

internal sealed class MacOSAutomaticPaste(
    IMacOSPasteNative native,
    MacOSClipboardSettings settings,
    IAsyncDelay delay)
{
    public IAutomaticPasteTarget? CaptureForegroundTarget() => native.CaptureForegroundTarget();

    public bool HasAccessibilityPermission => native.HasAccessibilityPermission();

    public async ValueTask<AutomaticPasteResult> TryPasteAsync(
        IAutomaticPasteTarget target,
        CancellationToken cancellationToken)
    {
        if (target is not MacOSAutomaticPasteTarget macTarget)
        {
            return ManualPaste(AutomaticPasteFailureReason.InvalidTarget);
        }

        if (!native.HasAccessibilityPermission())
        {
            // 不主动弹出系统授权框，也不尝试绕过 TCC。调用方展示固定降级文案，
            // 用户可在“隐私与安全性 > 辅助功能”中自行授权后重试。
            return ManualPaste(AutomaticPasteFailureReason.AccessibilityPermissionDenied);
        }

        if (!native.IsTargetAvailable(macTarget))
        {
            return ManualPaste(AutomaticPasteFailureReason.InvalidTarget);
        }

        if (!native.Activate(macTarget))
        {
            return ManualPaste(AutomaticPasteFailureReason.TargetActivationFailed);
        }

        for (int attempt = 0; attempt < settings.TargetActivationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (native.GetFrontmostProcessId() == macTarget.ProcessId)
            {
                return native.SendPasteShortcut()
                    ? new AutomaticPasteResult(AutomaticPasteStatus.Pasted)
                    : ManualPaste(AutomaticPasteFailureReason.InputInjectionBlocked);
            }

            await delay.DelayAsync(
                settings.TargetActivationPollInterval,
                cancellationToken).ConfigureAwait(false);
        }

        return ManualPaste(AutomaticPasteFailureReason.TargetActivationFailed);
    }

    private static AutomaticPasteResult ManualPaste(AutomaticPasteFailureReason reason) =>
        new(AutomaticPasteStatus.ManualPasteRequired, reason);
}
