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
    internal const int PrimaryRegistrationIdentifier = 0x5342;
    internal const int DoubleRegistrationIdentifier = 0x5343;
    internal const int AlternatePrimaryRegistrationIdentifier = 0x5352;
    internal const int AlternateDoubleRegistrationIdentifier = 0x5353;

    private readonly object _gate = new();
    private HotKeyRegistration? _primaryRegistration;
    private HotKeyRegistration? _doubleRegistration;

    public GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot)
    {
        lock (_gate)
        {
            return GetRegistration(slot)?.Gesture;
        }
    }

    internal int? GetCurrentIdentifier(GlobalHotKeySlot slot)
    {
        lock (_gate)
        {
            return GetRegistration(slot)?.Identifier;
        }
    }

    public GlobalHotKeyRegistrationResult Register(
        nint windowHandle,
        GlobalHotKeySlot slot,
        GlobalHotKeyGesture gesture)
    {
        lock (_gate)
        {
            HotKeyRegistration? current = GetRegistration(slot);
            if (current is HotKeyRegistration existing &&
                gesture.HasSameBinding(existing.Gesture))
            {
                SetRegistration(slot, existing with { Gesture = gesture });
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Registered);
            }

            GlobalHotKeySlot otherSlot = slot == GlobalHotKeySlot.Primary
                ? GlobalHotKeySlot.Double
                : GlobalHotKeySlot.Primary;
            if (GetRegistration(otherSlot) is HotKeyRegistration other &&
                gesture.HasSameBinding(other.Gesture))
            {
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Duplicate);
            }

            int identifier = GetNextIdentifier(slot, current?.Identifier);
            if (native.Register(
                    windowHandle,
                    identifier,
                    (uint)gesture.Modifiers,
                    gesture.VirtualKey))
            {
                if (current is not null && !native.Unregister(windowHandle, current.Identifier))
                {
                    int unregisterError = native.GetLastError();
                    native.Unregister(windowHandle, identifier);
                    return new GlobalHotKeyRegistrationResult(
                        GlobalHotKeyRegistrationStatus.Failed,
                        unregisterError);
                }

                SetRegistration(slot, new HotKeyRegistration(identifier, gesture));
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Registered);
            }

            int error = native.GetLastError();
            return new GlobalHotKeyRegistrationResult(
                error == WindowsNativeConstants.ErrorHotKeyAlreadyRegistered
                    ? GlobalHotKeyRegistrationStatus.Conflict
                    : GlobalHotKeyRegistrationStatus.Failed,
                error);
        }
    }

    public GlobalHotKeyRegistrationResult Clear(nint windowHandle, GlobalHotKeySlot slot)
    {
        lock (_gate)
        {
            HotKeyRegistration? current = GetRegistration(slot);
            if (current is null)
            {
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Registered);
            }

            if (!native.Unregister(windowHandle, current.Identifier))
            {
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Failed,
                    native.GetLastError());
            }

            SetRegistration(slot, null);
            return new GlobalHotKeyRegistrationResult(
                GlobalHotKeyRegistrationStatus.Registered);
        }
    }

    public void UnregisterAll(nint windowHandle)
    {
        lock (_gate)
        {
            if (_primaryRegistration is HotKeyRegistration primary)
            {
                native.Unregister(windowHandle, primary.Identifier);
                _primaryRegistration = null;
            }

            if (_doubleRegistration is HotKeyRegistration doubleRegistration)
            {
                native.Unregister(windowHandle, doubleRegistration.Identifier);
                _doubleRegistration = null;
            }
        }
    }

    internal static bool TryGetSlot(int identifier, out GlobalHotKeySlot slot)
    {
        switch (identifier)
        {
            case PrimaryRegistrationIdentifier:
            case AlternatePrimaryRegistrationIdentifier:
                slot = GlobalHotKeySlot.Primary;
                return true;
            case DoubleRegistrationIdentifier:
            case AlternateDoubleRegistrationIdentifier:
                slot = GlobalHotKeySlot.Double;
                return true;
            default:
                slot = default;
                return false;
        }
    }

    internal bool TryGetActiveSlot(int identifier, out GlobalHotKeySlot slot)
    {
        lock (_gate)
        {
            if (TryGetSlot(identifier, out slot) &&
                GetRegistration(slot)?.Identifier == identifier)
            {
                return true;
            }

            slot = default;
            return false;
        }
    }

    private HotKeyRegistration? GetRegistration(GlobalHotKeySlot slot) => slot switch
    {
        GlobalHotKeySlot.Primary => _primaryRegistration,
        GlobalHotKeySlot.Double => _doubleRegistration,
        _ => null,
    };

    private void SetRegistration(GlobalHotKeySlot slot, HotKeyRegistration? registration)
    {
        switch (slot)
        {
            case GlobalHotKeySlot.Primary:
                _primaryRegistration = registration;
                break;
            case GlobalHotKeySlot.Double:
                _doubleRegistration = registration;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private static int GetNextIdentifier(GlobalHotKeySlot slot, int? currentIdentifier) => slot switch
    {
        GlobalHotKeySlot.Primary => currentIdentifier == PrimaryRegistrationIdentifier
            ? AlternatePrimaryRegistrationIdentifier
            : PrimaryRegistrationIdentifier,
        GlobalHotKeySlot.Double => currentIdentifier == DoubleRegistrationIdentifier
            ? AlternateDoubleRegistrationIdentifier
            : DoubleRegistrationIdentifier,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    private sealed record HotKeyRegistration(int Identifier, GlobalHotKeyGesture Gesture);
}
