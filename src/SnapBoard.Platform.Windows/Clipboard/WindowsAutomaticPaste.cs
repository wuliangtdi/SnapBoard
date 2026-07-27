using System.Runtime.InteropServices;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Clipboard;

internal enum IntegrityComparison
{
    SameOrLower = 0,
    Higher = 1,
    Unknown = 2,
}

internal interface IWindowsPasteNative
{
    nint GetForegroundWindow();

    bool IsWindow(nint windowHandle);

    uint GetWindowProcessId(nint windowHandle);

    bool SetForegroundWindow(nint windowHandle);

    IntegrityComparison CompareIntegrity(uint targetProcessId);

    bool SendPasteShortcut();
}

internal sealed class WindowsPasteNative : IWindowsPasteNative
{
    public nint GetForegroundWindow() => WindowsNativeMethods.GetForegroundWindow();

    public bool IsWindow(nint windowHandle) => WindowsNativeMethods.IsWindow(windowHandle);

    public uint GetWindowProcessId(nint windowHandle)
    {
        WindowsNativeMethods.GetWindowThreadProcessId(windowHandle, out uint processId);
        return processId;
    }

    public bool SetForegroundWindow(nint windowHandle) =>
        WindowsNativeMethods.SetForegroundWindow(windowHandle);

    public IntegrityComparison CompareIntegrity(uint targetProcessId)
    {
        nint targetProcess = WindowsNativeMethods.OpenProcess(
            WindowsNativeConstants.ProcessQueryLimitedInformation,
            false,
            targetProcessId);
        if (targetProcess == 0)
        {
            return IntegrityComparison.Unknown;
        }

        try
        {
            if (!TryGetIntegrityLevel(WindowsNativeMethods.GetCurrentProcess(), out uint currentLevel) ||
                !TryGetIntegrityLevel(targetProcess, out uint targetLevel))
            {
                return IntegrityComparison.Unknown;
            }

            return targetLevel > currentLevel
                ? IntegrityComparison.Higher
                : IntegrityComparison.SameOrLower;
        }
        finally
        {
            WindowsNativeMethods.CloseHandle(targetProcess);
        }
    }

    public unsafe bool SendPasteShortcut()
    {
        bool controlAlreadyDown =
            (WindowsNativeMethods.GetAsyncKeyState(WindowsNativeConstants.VirtualKeyControl) & 0x8000) != 0;

        NativeInput* inputs = stackalloc NativeInput[4];
        uint count = 0;

        if (!controlAlreadyDown)
        {
            inputs[count++] = CreateKeyboardInput(
                WindowsNativeConstants.VirtualKeyControl,
                keyUp: false);
        }

        inputs[count++] = CreateKeyboardInput(WindowsNativeConstants.VirtualKeyV, keyUp: false);
        inputs[count++] = CreateKeyboardInput(WindowsNativeConstants.VirtualKeyV, keyUp: true);

        if (!controlAlreadyDown)
        {
            inputs[count++] = CreateKeyboardInput(
                WindowsNativeConstants.VirtualKeyControl,
                keyUp: true);
        }

        return WindowsNativeMethods.SendInput(count, inputs, sizeof(NativeInput)) == count;
    }

    private static unsafe bool TryGetIntegrityLevel(nint process, out uint integrityLevel)
    {
        integrityLevel = 0;
        if (!WindowsNativeMethods.OpenProcessToken(
                process,
                WindowsNativeConstants.TokenQuery,
                out nint token))
        {
            return false;
        }

        try
        {
            WindowsNativeMethods.GetTokenInformation(
                token,
                WindowsNativeConstants.TokenIntegrityLevel,
                null,
                0,
                out uint requiredLength);
            if (requiredLength == 0 || requiredLength > 4096)
            {
                return false;
            }

            byte[] buffer = new byte[requiredLength];
            fixed (byte* bufferPointer = buffer)
            {
                if (!WindowsNativeMethods.GetTokenInformation(
                        token,
                        WindowsNativeConstants.TokenIntegrityLevel,
                        bufferPointer,
                        requiredLength,
                        out _))
                {
                    return false;
                }

                TokenMandatoryLabel* label = (TokenMandatoryLabel*)bufferPointer;
                nint countPointer = WindowsNativeMethods.GetSidSubAuthorityCount(label->Label.Sid);
                if (countPointer == 0)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(countPointer);
                if (count == 0)
                {
                    return false;
                }

                nint levelPointer = WindowsNativeMethods.GetSidSubAuthority(
                    label->Label.Sid,
                    (uint)(count - 1));
                if (levelPointer == 0)
                {
                    return false;
                }

                integrityLevel = unchecked((uint)Marshal.ReadInt32(levelPointer));
                return true;
            }
        }
        finally
        {
            WindowsNativeMethods.CloseHandle(token);
        }
    }

    private static NativeInput CreateKeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = WindowsNativeConstants.InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? WindowsNativeConstants.KeyEventKeyUp : 0,
            },
        },
    };
}

internal sealed record WindowsAutomaticPasteTarget(
    nint WindowHandle,
    uint ProcessId) : IAutomaticPasteTarget;

internal sealed class WindowsAutomaticPaste(IWindowsPasteNative native)
{
    public IAutomaticPasteTarget? CaptureForegroundTarget()
    {
        nint windowHandle = native.GetForegroundWindow();
        if (windowHandle == 0 || !native.IsWindow(windowHandle))
        {
            return null;
        }

        uint processId = native.GetWindowProcessId(windowHandle);
        return processId == 0 ? null : new WindowsAutomaticPasteTarget(windowHandle, processId);
    }

    public ValueTask<AutomaticPasteResult> TryPasteAsync(
        IAutomaticPasteTarget target,
        CancellationToken cancellationToken) =>
        new(Task.Run(() => TryPasteCore(target, cancellationToken), cancellationToken));

    public ValueTask<ForegroundActivationResult> TryActivateTargetAsync(
        IAutomaticPasteTarget target,
        CancellationToken cancellationToken) =>
        new(Task.Run(() => TryActivateCore(target, cancellationToken), cancellationToken));

    private AutomaticPasteResult TryPasteCore(
        IAutomaticPasteTarget target,
        CancellationToken cancellationToken)
    {
        if (target is not WindowsAutomaticPasteTarget windowsTarget)
        {
            return ManualPaste(AutomaticPasteFailureReason.InvalidTarget);
        }

        if (!IsTargetValid(windowsTarget))
        {
            return new AutomaticPasteResult(
                AutomaticPasteStatus.TargetUnavailable,
                AutomaticPasteFailureReason.InvalidTarget);
        }

        IntegrityComparison integrity = native.CompareIntegrity(windowsTarget.ProcessId);
        if (integrity == IntegrityComparison.Higher)
        {
            // UIPI 明确禁止普通完整性进程向管理员窗口注入输入。剪贴板已经写好，
            // 此处只返回“已复制，请手动粘贴”，绝不尝试提权或绕过系统安全边界。
            return ManualPaste(AutomaticPasteFailureReason.HigherIntegrityTarget);
        }

        if (integrity == IntegrityComparison.Unknown)
        {
            // 无法读取目标令牌时采用保守降级，避免把权限探测失败误报为粘贴成功。
            return ManualPaste(AutomaticPasteFailureReason.IntegrityLevelUnavailable);
        }

        ForegroundActivationResult activation = TryActivateCore(windowsTarget, cancellationToken);
        if (activation.Status != ForegroundActivationStatus.Activated)
        {
            return activation.Status == ForegroundActivationStatus.TargetUnavailable
                ? new AutomaticPasteResult(
                    AutomaticPasteStatus.TargetUnavailable,
                    activation.FailureReason)
                : ManualPaste(activation.FailureReason);
        }

        // 激活等待期间目标 HWND 可能被销毁并复用。SendInput 前再次核对 HWND/PID，
        // 防止把粘贴快捷键发送给后来占用同一句柄的无关窗口。
        if (!IsTargetValid(windowsTarget))
        {
            return new AutomaticPasteResult(
                AutomaticPasteStatus.TargetUnavailable,
                AutomaticPasteFailureReason.InvalidTarget);
        }

        if (native.GetForegroundWindow() != windowsTarget.WindowHandle)
        {
            // 目标句柄仍有效并不代表它仍在前台。激活后若被系统或用户切走，必须停止注入，
            // 否则 Ctrl+V 会落入无关窗口；剪贴板已写好，按手动粘贴安全降级。
            return ManualPaste(AutomaticPasteFailureReason.TargetActivationFailed);
        }

        // SendInput 被 UIPI 阻止时没有可靠的专用错误码；返回数量不足一律按手动粘贴降级。
        return native.SendPasteShortcut()
            ? new AutomaticPasteResult(AutomaticPasteStatus.Pasted)
            : ManualPaste(AutomaticPasteFailureReason.InputInjectionBlocked);
    }

    private ForegroundActivationResult TryActivateCore(
        IAutomaticPasteTarget target,
        CancellationToken cancellationToken)
    {
        if (target is not WindowsAutomaticPasteTarget windowsTarget || !IsTargetValid(windowsTarget))
        {
            return new ForegroundActivationResult(
                ForegroundActivationStatus.TargetUnavailable,
                AutomaticPasteFailureReason.InvalidTarget);
        }

        native.SetForegroundWindow(windowsTarget.WindowHandle);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (native.GetForegroundWindow() == windowsTarget.WindowHandle)
            {
                break;
            }

            ClipboardRetryPolicy.Wait(TimeSpan.FromMilliseconds(40), cancellationToken);
        }

        if (native.GetForegroundWindow() != windowsTarget.WindowHandle)
        {
            return new ForegroundActivationResult(
                ForegroundActivationStatus.Failed,
                AutomaticPasteFailureReason.TargetActivationFailed);
        }

        return new ForegroundActivationResult(ForegroundActivationStatus.Activated);
    }

    private bool IsTargetValid(WindowsAutomaticPasteTarget target) =>
        native.IsWindow(target.WindowHandle) &&
        native.GetWindowProcessId(target.WindowHandle) == target.ProcessId;

    private static AutomaticPasteResult ManualPaste(AutomaticPasteFailureReason reason) =>
        new(AutomaticPasteStatus.ManualPasteRequired, reason);
}
