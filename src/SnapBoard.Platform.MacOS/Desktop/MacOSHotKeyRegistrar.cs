using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

internal interface IMacOSHotKeyRegistrar : IDisposable
{
    event Action? Pressed;

    GlobalHotKeyGesture? CurrentGesture { get; }

    int Register(GlobalHotKeyGesture gesture);

    void Unregister();
}

internal sealed class MacOSHotKeyRegistrar : IMacOSHotKeyRegistrar
{
    private const uint CommandKey = 1u << 8;
    private const uint ShiftKey = 1u << 9;
    private const uint OptionKey = 1u << 11;
    private const uint ControlKey = 1u << 12;
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 5;
    private const uint HotKeyExclusive = 1;
    private const uint HotKeySignature = 0x536E4264;

    private GCHandle _selfHandle;
    private nint _eventHandler;
    private nint _hotKey;
    private int _disposed;

    public event Action? Pressed;

    public GlobalHotKeyGesture? CurrentGesture { get; private set; }

    public unsafe MacOSHotKeyRegistrar()
    {
        NativeEventTypeSpec eventType = new()
        {
            EventClass = EventClassKeyboard,
            EventKind = EventHotKeyPressed,
        };
        _selfHandle = GCHandle.Alloc(this);
        int status = MacOSNativeMethods.InstallEventHandler(
            MacOSNativeMethods.GetApplicationEventTarget(),
            &HandleCarbonEvent,
            1,
            &eventType,
            GCHandle.ToIntPtr(_selfHandle),
            out _eventHandler);
        if (status != 0)
        {
            _selfHandle.Free();
            throw new InvalidOperationException(
                $"Carbon event handler installation failed with OSStatus {status}.");
        }
    }

    public int Register(GlobalHotKeyGesture gesture)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_hotKey != 0)
        {
            return CurrentGesture == gesture ? 0 : -9878;
        }

        NativeEventHotKeyId identifier = new()
        {
            Signature = HotKeySignature,
            Id = 1,
        };
        int status = MacOSNativeMethods.RegisterEventHotKey(
            gesture.VirtualKey,
            ToCarbonModifiers(gesture.Modifiers),
            identifier,
            MacOSNativeMethods.GetApplicationEventTarget(),
            HotKeyExclusive,
            out nint reference);
        if (status == 0)
        {
            _hotKey = reference;
            CurrentGesture = gesture;
        }

        return status;
    }

    public void Unregister()
    {
        if (_hotKey == 0)
        {
            CurrentGesture = null;
            return;
        }

        if (MacOSNativeMethods.UnregisterEventHotKey(_hotKey) != 0)
        {
            // 进程退出或系统已撤销注册时仍需清除托管句柄状态，防止重复释放。
        }
        _hotKey = 0;
        CurrentGesture = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Carbon 的注册和事件处理器都属于应用主事件循环，必须在构造它们的主线程释放。
        Unregister();
        if (_eventHandler != 0)
        {
            if (MacOSNativeMethods.RemoveEventHandler(_eventHandler) != 0)
            {
                // 应用事件目标销毁在先时 Carbon 可能拒绝移除；本地引用仍只释放一次。
            }
            _eventHandler = 0;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HandleCarbonEvent(nint _, nint __, nint userData)
    {
        try
        {
            if (userData != 0 &&
                GCHandle.FromIntPtr(userData).Target is MacOSHotKeyRegistrar registrar)
            {
                // Carbon 回调只通知有界事件泵，不创建窗口、不触碰 Avalonia。
                registrar.Pressed?.Invoke();
            }
        }
        catch
        {
            // 托管异常不得穿过 Native AOT 的 Carbon 回调边界。
        }

        return 0;
    }
}
