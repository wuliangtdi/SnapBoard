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
        delegate* unmanaged[Cdecl]<nint, void> modifierHandler,
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

    bool IsKeyPressed(uint virtualKey);
}

internal sealed unsafe class MacOSHotKeyNative : IMacOSHotKeyNative
{
    private const int CombinedSessionEventState = 0;
    private const int LocalMonitorInstallationFailed = -1;
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventRawKeyModifiersChanged = 4;
    private const uint EventHotKeyPressed = 5;
    private const uint EventHotKeyReleased = 6;
    private const uint EventParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyId = 0x686B6964;
    private const uint HotKeyExclusive = 1;
    private const nuint FlagsChangedEventMask = 1u << 12;
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private nint _inactiveModifierEventHandler;
    private nint _localModifierMonitor;
    private nint _localModifierMonitorDescriptor;

    public int InstallEventHandler(
        delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
        delegate* unmanaged[Cdecl]<nint, void> modifierHandler,
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
        int status = MacOSNativeMethods.InstallEventHandler(
            MacOSNativeMethods.GetApplicationEventTarget(),
            handler,
            2,
            eventTypes,
            userData,
            out handlerReference);
        if (status != 0)
        {
            return status;
        }

        NativeEventTypeSpec modifierEvent = new()
        {
            EventClass = EventClassKeyboard,
            EventKind = EventRawKeyModifiersChanged,
        };
        status = InstallLocalModifierMonitor(modifierHandler, userData);
        if (status != 0)
        {
            return RollBackEventHandlers(status, ref handlerReference);
        }

        status = MacOSNativeMethods.InstallEventHandler(
            MacOSNativeMethods.GetEventMonitorTarget(),
            handler,
            1,
            &modifierEvent,
            userData,
            out _inactiveModifierEventHandler);
        if (status != 0)
        {
            return RollBackEventHandlers(status, ref handlerReference);
        }

        return 0;
    }

    public int RemoveEventHandler(nint handlerReference)
    {
        RemoveLocalModifierMonitor();
        int inactiveStatus = RemoveEventHandler(ref _inactiveModifierEventHandler);
        int hotKeyStatus = handlerReference == 0
            ? 0
            : MacOSNativeMethods.RemoveEventHandler(handlerReference);
        return hotKeyStatus != 0
            ? hotKeyStatus
            : inactiveStatus;
    }

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
        if (eventKind == EventRawKeyModifiersChanged)
        {
            return true;
        }

        return MacOSNativeMethods.GetEventParameter(
            eventReference,
            EventParamDirectObject,
            TypeEventHotKeyId,
            null,
            (nuint)Unsafe.SizeOf<NativeEventHotKeyId>(),
            null,
            Unsafe.AsPointer(ref identifier)) == 0;
    }

    public bool IsKeyPressed(uint virtualKey) =>
        virtualKey <= ushort.MaxValue &&
        MacOSNativeMethods.CGEventSourceKeyState(
            CombinedSessionEventState,
            (ushort)virtualKey);

    private int InstallLocalModifierMonitor(
        delegate* unmanaged[Cdecl]<nint, void> modifierHandler,
        nint userData)
    {
        nint stackBlockClass = GetStackBlockClass();
        _localModifierMonitorDescriptor = (nint)NativeMemory.Alloc(
            (nuint)sizeof(NativeBlockDescriptor));
        NativeBlockDescriptor* descriptor =
            (NativeBlockDescriptor*)_localModifierMonitorDescriptor;
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)sizeof(LocalModifierMonitorBlock);

        LocalModifierMonitorBlock block = new()
        {
            Isa = stackBlockClass,
            Invoke = &InvokeLocalModifierMonitor,
            Descriptor = descriptor,
            Handler = modifierHandler,
            UserData = userData,
        };
        _localModifierMonitor = MacOSNativeMethods.SendIntPtrWithNUIntIntPtr(
            ObjectiveC.GetRequiredClass("NSEvent"),
            ObjectiveC.GetSelector("addLocalMonitorForEventsMatchingMask:handler:"),
            FlagsChangedEventMask,
            (nint)(&block));
        if (_localModifierMonitor != 0)
        {
            return 0;
        }

        NativeMemory.Free((void*)_localModifierMonitorDescriptor);
        _localModifierMonitorDescriptor = 0;
        return LocalMonitorInstallationFailed;
    }

    private void RemoveLocalModifierMonitor()
    {
        if (_localModifierMonitor != 0)
        {
            MacOSNativeMethods.SendVoidWithIntPtr(
                ObjectiveC.GetRequiredClass("NSEvent"),
                ObjectiveC.GetSelector("removeMonitor:"),
                _localModifierMonitor);
            _localModifierMonitor = 0;
        }

        if (_localModifierMonitorDescriptor != 0)
        {
            NativeMemory.Free((void*)_localModifierMonitorDescriptor);
            _localModifierMonitorDescriptor = 0;
        }
    }

    private int RollBackEventHandlers(int failureStatus, ref nint handlerReference)
    {
        if (RemoveEventHandler(handlerReference) == 0)
        {
            handlerReference = 0;
        }

        return failureStatus;
    }

    private static int RemoveEventHandler(ref nint handlerReference)
    {
        if (handlerReference == 0)
        {
            return 0;
        }

        int status = MacOSNativeMethods.RemoveEventHandler(handlerReference);
        if (status == 0)
        {
            handlerReference = 0;
        }

        return status;
    }

    private static nint GetStackBlockClass()
    {
        nint library = NativeLibrary.Load(LibSystem);
        try
        {
            return NativeLibrary.GetExport(library, "_NSConcreteStackBlock");
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint InvokeLocalModifierMonitor(nint blockReference, nint eventReference)
    {
        try
        {
            LocalModifierMonitorBlock* block =
                (LocalModifierMonitorBlock*)blockReference;
            block->Handler(block->UserData);
        }
        catch
        {
            // 托管异常不得穿过 AppKit block 回调边界。
        }

        return eventReference;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalModifierMonitorBlock
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public delegate* unmanaged[Cdecl]<nint, nint, nint> Invoke;
        public NativeBlockDescriptor* Descriptor;
        public delegate* unmanaged[Cdecl]<nint, void> Handler;
        public nint UserData;
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
    private const uint EventRawKeyModifiersChanged = 4;
    private const uint EventHotKeyPressed = 5;
    private const uint EventHotKeyReleased = 6;
    private const uint HotKeySignature = 0x536E4264;
    private const GlobalHotKeyModifiers UserModifiers =
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Meta;

    private readonly object _gate = new();
    private readonly IMacOSHotKeyNative _native;
    private GCHandle _selfHandle;
    private nint _eventHandler;
    private HotKeyRegistration? _primaryRegistration;
    private HotKeyRegistration? _doubleRegistration;
    private bool _primaryHeld;
    private bool _primaryModifierArmed;
    private bool _doubleHeld;
    private bool _doubleModifierArmed;
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
            &HandleLocalModifierEvent,
            GCHandle.ToIntPtr(_selfHandle),
            out _eventHandler);
        if (status != 0)
        {
            bool handlerRemoved = _eventHandler == 0 ||
                _native.RemoveEventHandler(_eventHandler) == 0;
            if (handlerRemoved)
            {
                _selfHandle.Free();
            }

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
                ToCarbonModifiers(gesture),
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
            SetModifierArmed(slot, false);
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
            SetModifierArmed(slot, false);
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
            _primaryModifierArmed = false;
            _doubleModifierArmed = false;
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
        if (eventKind == EventRawKeyModifiersChanged)
        {
            ProcessModifierStateChange();
            return;
        }

        MacOSHotKeyNativeEvent? notification = null;
        lock (_gate)
        {
            if (identifier.Signature != HotKeySignature ||
                !TryGetSlot(identifier.Id, out GlobalHotKeySlot slot) ||
                GetRegistration(slot) is not HotKeyRegistration registration ||
                RequiresModifierEvents(registration))
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

    private void ProcessModifierStateChange()
    {
        List<MacOSHotKeyNativeEvent>? notifications = null;
        try
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                ProcessModifierHotKeyState(GlobalHotKeySlot.Primary, ref notifications);
                ProcessModifierHotKeyState(GlobalHotKeySlot.Double, ref notifications);
            }
        }
        catch
        {
            // 读取按键状态失败只让本轮失效，不能终止进程或影响 Carbon 常规快捷键。
            return;
        }

        if (notifications is null)
        {
            return;
        }

        foreach (MacOSHotKeyNativeEvent notification in notifications)
        {
            Triggered?.Invoke(notification);
        }
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

    private static uint ToCarbonModifiers(GlobalHotKeyGesture gesture)
    {
        // 修饰键作为主键时，它由本次按键变化触发，不能同时作为预先按住的
        // Carbon 匹配标志。持久化手势仍保留完整标志用于校验和显示。
        GlobalHotKeyModifiers modifiers = gesture.Modifiers &
            ~MacOSHotKeyKeyMap.GetRequiredMainKeyModifier(gesture.VirtualKey);
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

    private void ProcessModifierHotKeyState(
        GlobalHotKeySlot slot,
        ref List<MacOSHotKeyNativeEvent>? notifications)
    {
        if (GetRegistration(slot) is not HotKeyRegistration registration ||
            !RequiresModifierEvents(registration))
        {
            return;
        }

        bool isPressed = _native.IsKeyPressed(registration.Gesture.VirtualKey);
        if (!isPressed)
        {
            bool shouldTrigger = GetHeld(slot) && GetModifierArmed(slot);
            SetHeld(slot, false);
            SetModifierArmed(slot, false);
            if (shouldTrigger)
            {
                notifications ??= [];
                notifications.Add(new MacOSHotKeyNativeEvent(slot, IsRepeat: false));
            }

            return;
        }

        if (GetHeld(slot))
        {
            return;
        }

        SetHeld(slot, true);
        SetModifierArmed(slot, DoModifierStatesMatch(registration.Gesture));
    }

    private bool DoModifierStatesMatch(GlobalHotKeyGesture gesture)
    {
        GlobalHotKeyModifiers mainKeyModifier =
            MacOSHotKeyKeyMap.GetRequiredMainKeyModifier(gesture.VirtualKey);
        GlobalHotKeyModifiers expected = gesture.Modifiers &
            UserModifiers &
            ~mainKeyModifier;
        return DoesModifierStateMatch(
                GlobalHotKeyModifiers.Meta,
                mainKeyModifier,
                expected,
                0x37,
                0x36) &&
            DoesModifierStateMatch(
                GlobalHotKeyModifiers.Alt,
                mainKeyModifier,
                expected,
                0x3A,
                0x3D) &&
            DoesModifierStateMatch(
                GlobalHotKeyModifiers.Control,
                mainKeyModifier,
                expected,
                0x3B,
                0x3E) &&
            DoesModifierStateMatch(
                GlobalHotKeyModifiers.Shift,
                mainKeyModifier,
                expected,
                0x38,
                0x3C);
    }

    private bool DoesModifierStateMatch(
        GlobalHotKeyModifiers modifier,
        GlobalHotKeyModifiers mainKeyModifier,
        GlobalHotKeyModifiers expected,
        uint leftKey,
        uint rightKey) =>
        modifier == mainKeyModifier ||
        expected.HasFlag(modifier) == IsEitherKeyPressed(leftKey, rightKey);

    private bool IsEitherKeyPressed(uint leftKey, uint rightKey) =>
        _native.IsKeyPressed(leftKey) || _native.IsKeyPressed(rightKey);

    private static bool RequiresModifierEvents(HotKeyRegistration registration) =>
        MacOSHotKeyKeyMap.GetRequiredMainKeyModifier(
            registration.Gesture.VirtualKey) != GlobalHotKeyModifiers.None;

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

    private bool GetModifierArmed(GlobalHotKeySlot slot) => slot switch
    {
        GlobalHotKeySlot.Primary => _primaryModifierArmed,
        GlobalHotKeySlot.Double => _doubleModifierArmed,
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

    private void SetModifierArmed(GlobalHotKeySlot slot, bool armed)
    {
        switch (slot)
        {
            case GlobalHotKeySlot.Primary:
                _primaryModifierArmed = armed;
                break;
            case GlobalHotKeySlot.Double:
                _doubleModifierArmed = armed;
                break;
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
    private static void HandleLocalModifierEvent(nint userData)
    {
        try
        {
            if (userData != 0 &&
                GCHandle.FromIntPtr(userData).Target is MacOSHotKeyRegistrar registrar)
            {
                registrar.ProcessModifierStateChange();
            }
        }
        catch
        {
            // 托管异常不得穿过 AppKit 本地事件回调边界。
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HandleCarbonEvent(nint _, nint eventReference, nint userData)
    {
        const int eventNotHandledStatus = -9874;
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
                // 常规热键只处理两个注册 ID；纯修饰键只接收状态变化，不读取普通按键。
                registrar.ProcessNativeEvent(eventKind, identifier);
                return eventKind == EventRawKeyModifiersChanged
                    ? eventNotHandledStatus
                    : 0;
            }
        }
        catch
        {
            // 托管异常不得穿过 Native AOT 的 Carbon 回调边界。
        }

        return eventNotHandledStatus;
    }

    private sealed record HotKeyRegistration(
        nint Reference,
        GlobalHotKeyGesture Gesture);
}
