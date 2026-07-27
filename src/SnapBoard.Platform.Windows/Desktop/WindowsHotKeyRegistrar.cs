using System.Runtime.InteropServices;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Desktop;

internal interface IWindowsHotKeyNative
{
    bool Register(nint windowHandle, int identifier, uint modifiers, uint virtualKey);

    bool Unregister(nint windowHandle, int identifier);

    int GetLastError();
}

internal sealed class WindowsHotKeyNative : IWindowsHotKeyNative
{
    public bool Register(nint windowHandle, int identifier, uint modifiers, uint virtualKey) =>
        WindowsNativeMethods.RegisterHotKey(windowHandle, identifier, modifiers, virtualKey);

    public bool Unregister(nint windowHandle, int identifier) =>
        WindowsNativeMethods.UnregisterHotKey(windowHandle, identifier);

    public int GetLastError() => Marshal.GetLastPInvokeError();
}

internal sealed class WindowsHotKeyRegistrar(IWindowsHotKeyNative native)
{
    internal const int RegistrationIdentifier = 0x5342;
    private readonly object _gate = new();
    private GlobalHotKeyGesture? _currentGesture;

    public GlobalHotKeyGesture? CurrentGesture
    {
        get
        {
            lock (_gate)
            {
                return _currentGesture;
            }
        }
    }

    public GlobalHotKeyRegistrationResult Register(
        nint windowHandle,
        GlobalHotKeyGesture gesture)
    {
        lock (_gate)
        {
            if (_currentGesture == gesture)
            {
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Registered);
            }

            GlobalHotKeyGesture? previous = _currentGesture;
            if (previous is not null)
            {
                native.Unregister(windowHandle, RegistrationIdentifier);
                _currentGesture = null;
            }

            if (native.Register(
                    windowHandle,
                    RegistrationIdentifier,
                    (uint)gesture.Modifiers,
                    gesture.VirtualKey))
            {
                _currentGesture = gesture;
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Registered);
            }

            int error = native.GetLastError();
            if (previous is not null && native.Register(
                    windowHandle,
                    RegistrationIdentifier,
                    (uint)previous.Value.Modifiers,
                    previous.Value.VirtualKey))
            {
                // 新组合被其他进程占用时立即恢复旧绑定，避免一次设置失败让快捷入口消失。
                _currentGesture = previous;
            }

            return new GlobalHotKeyRegistrationResult(
                error == WindowsNativeConstants.ErrorHotKeyAlreadyRegistered
                    ? GlobalHotKeyRegistrationStatus.Conflict
                    : GlobalHotKeyRegistrationStatus.Failed,
                error);
        }
    }

    public void Unregister(nint windowHandle)
    {
        lock (_gate)
        {
            if (_currentGesture is null)
            {
                return;
            }

            native.Unregister(windowHandle, RegistrationIdentifier);
            _currentGesture = null;
        }
    }
}
