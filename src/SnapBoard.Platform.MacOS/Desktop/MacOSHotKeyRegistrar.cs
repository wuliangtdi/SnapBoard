using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

internal readonly record struct MacOSHotKeyNativeEvent(
    GlobalHotKeySlot Source,
    bool IsRepeat);

internal interface IMacOSHotKeyRegistrar : IDisposable
{
    event Action<MacOSHotKeyNativeEvent>? Triggered;

    GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot);

    GlobalHotKeyRegistrationResult Register(
        GlobalHotKeySlot slot,
        GlobalHotKeyGesture gesture);

    GlobalHotKeyRegistrationResult Clear(GlobalHotKeySlot slot);

    void UnregisterAll();
}

internal unsafe interface IMacOSHotKeyNative
{
    int InstallEventHandler(
        delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
        nint userData,
        out nint handlerReference);

    int RemoveEventHandler(nint handlerReference);

    int Register(
        uint virtualKey,
        uint modifiers,
        NativeEventHotKeyId identifier,
        out nint hotKeyReference);

    int Unregister(nint hotKeyReference);

    bool TryReadEvent(
        nint eventReference,
        out uint eventKind,
        out NativeEventHotKeyId identifier);
}

internal sealed unsafe class MacOSHotKeyNative : IMacOSHotKeyNative
{
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 5;
    private const uint EventHotKeyReleased = 6;
    private const uint EventParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyId = 0x686B6964;
    private const uint HotKeyExclusive = 1;

    public int InstallEventHandler(
        delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
        nint userData,
        out nint handlerReference)
    {
        NativeEventTypeSpec* eventTypes = stackalloc NativeEventTypeSpec[2];
        eventTypes[0] = new NativeEventTypeSpec
        {
            EventClass = EventClassKeyboard,
            EventKind = EventHotKeyPressed,
        };
        eventTypes[1] = new NativeEventTypeSpec
        {
            EventClass = EventClassKeyboard,
            EventKind = EventHotKeyReleased,
        };
        return MacOSNativeMethods.InstallEventHandler(
            MacOSNativeMethods.GetApplicationEventTarget(),
            handler,
            2,
            eventTypes,
            userData,
            out handlerReference);
    }

    public int RemoveEventHandler(nint handlerReference) =>
        MacOSNativeMethods.RemoveEventHandler(handlerReference);

    public int Register(
        uint virtualKey,
        uint modifiers,
        NativeEventHotKeyId identifier,
        out nint hotKeyReference) => MacOSNativeMethods.RegisterEventHotKey(
            virtualKey,
            modifiers,
            identifier,
            MacOSNativeMethods.GetApplicationEventTarget(),
            HotKeyExclusive,
            out hotKeyReference);

    public int Unregister(nint hotKeyReference) =>
        MacOSNativeMethods.UnregisterEventHotKey(hotKeyReference);

    public bool TryReadEvent(
        nint eventReference,
        out uint eventKind,
        out NativeEventHotKeyId identifier)
    {
        eventKind = MacOSNativeMethods.GetEventKind(eventReference);
        identifier = default;
        return MacOSNativeMethods.GetEventParameter(
            eventReference,
            EventParamDirectObject,
            TypeEventHotKeyId,
            null,
            (nuint)Unsafe.SizeOf<NativeEventHotKeyId>(),
            null,
            Unsafe.AsPointer(ref identifier)) == 0;
    }
}

internal sealed class MacOSHotKeyRegistrar : IMacOSHotKeyRegistrar
{
    internal const uint PrimaryHotKeyIdentifier = 1;
    internal const uint DoubleHotKeyIdentifier = 2;

    private const int ConflictStatus = -9878;
    private const uint CommandKey = 1u << 8;
    private const uint ShiftKey = 1u << 9;
    private const uint OptionKey = 1u << 11;
    private const uint ControlKey = 1u << 12;
    private const uint EventHotKeyPressed = 5;
    private const uint EventHotKeyReleased = 6;
    private const uint HotKeySignature = 0x536E4264;

    private readonly object _gate = new();
    private readonly IMacOSHotKeyNative _native;
    private GCHandle _selfHandle;
    private nint _eventHandler;
    private HotKeyRegistration? _primaryRegistration;
    private HotKeyRegistration? _doubleRegistration;
    private bool _primaryHeld;
    private bool _doubleHeld;
    private int _disposed;

    public event Action<MacOSHotKeyNativeEvent>? Triggered;

    public MacOSHotKeyRegistrar()
        : this(new MacOSHotKeyNative())
    {
    }

    internal unsafe MacOSHotKeyRegistrar(IMacOSHotKeyNative native)
    {
        _native = native;
        _selfHandle = GCHandle.Alloc(this);
        int status = _native.InstallEventHandler(
            &HandleCarbonEvent,
            GCHandle.ToIntPtr(_selfHandle),
            out _eventHandler);
        if (status != 0)
        {
            _selfHandle.Free();
            throw new InvalidOperationException(
                $"Carbon event handler installation failed with OSStatus {status}.");
        }
    }

    public GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot)
    {
        lock (_gate)
        {
            return GetRegistration(slot)?.Gesture;
        }
    }

    public GlobalHotKeyRegistrationResult Register(
        GlobalHotKeySlot slot,
        GlobalHotKeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Enum.IsDefined(slot))
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Failed);
        }

        lock (_gate)
        {
            HotKeyRegistration? current = GetRegistration(slot);
            if (current is HotKeyRegistration existing &&
                gesture.HasSameBinding(existing.Gesture))
            {
                SetRegistration(slot, existing with { Gesture = gesture });
                return Registered();
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

            NativeEventHotKeyId identifier = CreateIdentifier(slot);
            int status = _native.Register(
                gesture.VirtualKey,
                ToCarbonModifiers(gesture.Modifiers),
                identifier,
                out nint reference);
            if (status != 0)
            {
                return new GlobalHotKeyRegistrationResult(
                    status == ConflictStatus
                        ? GlobalHotKeyRegistrationStatus.Conflict
                        : GlobalHotKeyRegistrationStatus.Failed,
                    status);
            }

            if (current is HotKeyRegistration previous &&
                _native.Unregister(previous.Reference) is int unregisterStatus and not 0)
            {
                _native.Unregister(reference);
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Failed,
                    unregisterStatus);
            }

            SetRegistration(slot, new HotKeyRegistration(reference, gesture));
            SetHeld(slot, false);
            return Registered();
        }
    }

    public GlobalHotKeyRegistrationResult Clear(GlobalHotKeySlot slot)
    {
        if (!Enum.IsDefined(slot))
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Failed);
        }

        lock (_gate)
        {
            HotKeyRegistration? current = GetRegistration(slot);
            if (current is null)
            {
                return Registered();
            }

            int status = _native.Unregister(current.Reference);
            if (status != 0)
            {
                return new GlobalHotKeyRegistrationResult(
                    GlobalHotKeyRegistrationStatus.Failed,
                    status);
            }

            SetRegistration(slot, null);
            SetHeld(slot, false);
            return Registered();
        }
    }

    public void UnregisterAll()
    {
        lock (_gate)
        {
            UnregisterForDispose(_primaryRegistration);
            UnregisterForDispose(_doubleRegistration);
            _primaryRegistration = null;
            _doubleRegistration = null;
            _primaryHeld = false;
            _doubleHeld = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Carbon 注册与事件处理器均属于应用主事件循环，必须在创建它们的主线程释放。
        UnregisterAll();
        if (_eventHandler != 0)
        {
            _native.RemoveEventHandler(_eventHandler);
            _eventHandler = 0;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    internal void ProcessNativeEvent(uint eventKind, NativeEventHotKeyId identifier)
    {
        MacOSHotKeyNativeEvent? notification = null;
        lock (_gate)
        {
            if (identifier.Signature != HotKeySignature ||
                !TryGetSlot(identifier.Id, out GlobalHotKeySlot slot) ||
                GetRegistration(slot) is null)
            {
                return;
            }

            if (eventKind == EventHotKeyReleased)
            {
                SetHeld(slot, false);
                return;
            }

            if (eventKind != EventHotKeyPressed)
            {
                return;
            }

            bool isRepeat = GetHeld(slot);
            SetHeld(slot, true);
            notification = new MacOSHotKeyNativeEvent(slot, isRepeat);
        }

        Triggered?.Invoke(notification.Value);
    }

    internal static NativeEventHotKeyId CreateIdentifier(GlobalHotKeySlot slot) => new()
    {
        Signature = HotKeySignature,
        Id = slot switch
        {
            GlobalHotKeySlot.Primary => PrimaryHotKeyIdentifier,
            GlobalHotKeySlot.Double => DoubleHotKeyIdentifier,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        },
    };

    private static bool TryGetSlot(uint identifier, out GlobalHotKeySlot slot)
    {
        switch (identifier)
        {
            case PrimaryHotKeyIdentifier:
                slot = GlobalHotKeySlot.Primary;
                return true;
            case DoubleHotKeyIdentifier:
                slot = GlobalHotKeySlot.Double;
                return true;
            default:
                slot = default;
                return false;
        }
    }

    private static uint ToCarbonModifiers(GlobalHotKeyModifiers modifiers)
    {
        uint native = 0;
        if (modifiers.HasFlag(GlobalHotKeyModifiers.Meta))
        {
            native |= CommandKey;
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Shift))
        {
            native |= ShiftKey;
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Alt))
        {
            native |= OptionKey;
        }

        if (modifiers.HasFlag(GlobalHotKeyModifiers.Control))
        {
            native |= ControlKey;
        }

        return native;
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

    private bool GetHeld(GlobalHotKeySlot slot) => slot switch
    {
        GlobalHotKeySlot.Primary => _primaryHeld,
        GlobalHotKeySlot.Double => _doubleHeld,
        _ => false,
    };

    private void SetHeld(GlobalHotKeySlot slot, bool held)
    {
        switch (slot)
        {
            case GlobalHotKeySlot.Primary:
                _primaryHeld = held;
                break;
            case GlobalHotKeySlot.Double:
                _doubleHeld = held;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private void UnregisterForDispose(HotKeyRegistration? registration)
    {
        if (registration is HotKeyRegistration value)
        {
            _native.Unregister(value.Reference);
        }
    }

    private static GlobalHotKeyRegistrationResult Registered() => new(
        GlobalHotKeyRegistrationStatus.Registered);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HandleCarbonEvent(nint _, nint eventReference, nint userData)
    {
        try
        {
            if (eventReference != 0 &&
                userData != 0 &&
                GCHandle.FromIntPtr(userData).Target is MacOSHotKeyRegistrar registrar &&
                registrar._native.TryReadEvent(
                    eventReference,
                    out uint eventKind,
                    out NativeEventHotKeyId identifier))
            {
                // 只处理这两个已注册 ID；release 状态用于识别系统重复，不监听其他按键。
                registrar.ProcessNativeEvent(eventKind, identifier);
            }
        }
        catch
        {
            // 托管异常不得穿过 Native AOT 的 Carbon 回调边界。
        }

        return 0;
    }

    private sealed record HotKeyRegistration(
        nint Reference,
        GlobalHotKeyGesture Gesture);
}
