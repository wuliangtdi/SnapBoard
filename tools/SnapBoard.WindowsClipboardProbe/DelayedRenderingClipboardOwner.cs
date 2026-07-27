using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.WindowsClipboardProbe;

[SupportedOSPlatform("windows")]
internal sealed class DelayedRenderingClipboardOwner(string text) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _windowClassName = $"SnapBoard.DelayedOwner.{Guid.NewGuid():N}";
    private Thread? _thread;
    private nint _windowHandle;
    private int _state;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state != 0)
            {
                throw new InvalidOperationException("The delayed clipboard owner can only start once.");
            }

            _state = 1;
            _thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "SnapBoard delayed clipboard owner",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        nint windowHandle;
        lock (_gate)
        {
            if (_state == 0)
            {
                _state = 3;
                _started.TrySetException(new ObjectDisposedException(nameof(DelayedRenderingClipboardOwner)));
                _stopped.TrySetResult();
                return;
            }

            if (_state == 3)
            {
                windowHandle = 0;
            }
            else
            {
                _state = 2;
                windowHandle = _windowHandle;
            }
        }

        if (windowHandle != 0)
        {
            WindowsNativeMethods.PostMessage(
                windowHandle,
                WindowsNativeConstants.WindowMessageClose,
                0,
                0);
        }

        await _stopped.Task.ConfigureAwait(false);
    }

    private void RunMessageLoop()
    {
        GCHandle selfHandle = default;
        ushort classAtom = 0;
        nint instance = 0;

        try
        {
            instance = WindowsNativeMethods.GetModuleHandle(null);
            if (instance == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            selfHandle = GCHandle.Alloc(this);
            classAtom = RegisterWindowClass(instance);
            if (classAtom == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            nint windowHandle = WindowsNativeMethods.CreateWindowEx(
                0,
                _windowClassName,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                instance,
                GCHandle.ToIntPtr(selfHandle));
            if (windowHandle == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            lock (_gate)
            {
                _windowHandle = windowHandle;
            }

            DeclareDelayedUnicodeText(windowHandle);
            _started.TrySetResult();

            while (true)
            {
                int result = WindowsNativeMethods.GetMessage(out NativeMessage message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                WindowsNativeMethods.TranslateMessage(in message);
                WindowsNativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
        }
        finally
        {
            nint windowHandle;
            lock (_gate)
            {
                windowHandle = _windowHandle;
            }

            if (windowHandle != 0 && WindowsNativeMethods.IsWindow(windowHandle))
            {
                WindowsNativeMethods.DestroyWindow(windowHandle);
            }

            if (classAtom != 0 && instance != 0)
            {
                WindowsNativeMethods.UnregisterClass(_windowClassName, instance);
            }

            if (selfHandle.IsAllocated)
            {
                selfHandle.Free();
            }

            lock (_gate)
            {
                _windowHandle = 0;
                _state = 3;
            }

            _stopped.TrySetResult();
        }
    }

    private unsafe ushort RegisterWindowClass(nint instance)
    {
        fixed (char* className = _windowClassName)
        {
            WindowClassEx windowClass = new()
            {
                Size = (uint)sizeof(WindowClassEx),
                WindowProcedure = &WindowProcedure,
                Instance = instance,
                ClassName = className,
            };
            return WindowsNativeMethods.RegisterClassEx(&windowClass);
        }
    }

    private static void DeclareDelayedUnicodeText(nint windowHandle)
    {
        if (!WindowsNativeMethods.OpenClipboard(windowHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!WindowsNativeMethods.EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // hMem=NULL 是 Win32 延迟渲染协议，不是写入失败。系统记录格式和 owner HWND，
            // 直到消费者调用 GetClipboardData 时再向该窗口同步发送 WM_RENDERFORMAT。
            WindowsNativeMethods.SetClipboardData(
                WindowsNativeConstants.ClipboardFormatUnicodeText,
                0);
        }
        finally
        {
            WindowsNativeMethods.CloseClipboard();
        }

        if (!WindowsNativeMethods.IsClipboardFormatAvailable(
                WindowsNativeConstants.ClipboardFormatUnicodeText))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private unsafe void RenderUnicodeText()
    {
        nuint bytes = checked((nuint)((text.Length + 1) * sizeof(char)));
        nint memoryHandle = WindowsNativeMethods.GlobalAlloc(
            WindowsNativeConstants.GlobalMemoryMoveable |
            WindowsNativeConstants.GlobalMemoryZeroInitialize,
            bytes);
        if (memoryHandle == 0)
        {
            return;
        }

        bool ownershipTransferred = false;
        try
        {
            nint pointer = WindowsNativeMethods.GlobalLock(memoryHandle);
            if (pointer == 0)
            {
                return;
            }

            try
            {
                Span<char> destination = new((void*)pointer, text.Length + 1);
                text.AsSpan().CopyTo(destination);
                destination[text.Length] = '\0';
            }
            finally
            {
                WindowsNativeMethods.GlobalUnlock(memoryHandle);
            }

            ownershipTransferred = WindowsNativeMethods.SetClipboardData(
                WindowsNativeConstants.ClipboardFormatUnicodeText,
                memoryHandle) != 0;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                WindowsNativeMethods.GlobalFree(memoryHandle);
            }
        }
    }

    private void RenderAllFormats(nint windowHandle)
    {
        if (WindowsNativeMethods.GetClipboardOwner() != windowHandle ||
            !WindowsNativeMethods.OpenClipboard(windowHandle))
        {
            return;
        }

        try
        {
            RenderUnicodeText();
        }
        finally
        {
            WindowsNativeMethods.CloseClipboard();
        }
    }

    private void ClearWindowHandle()
    {
        lock (_gate)
        {
            _windowHandle = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint WindowProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam)
    {
        try
        {
            nint statePointer;
            if (message == WindowsNativeConstants.WindowMessageNonClientCreate)
            {
                CreateStruct* createStruct = (CreateStruct*)lParam;
                statePointer = createStruct->CreateParameters;
                WindowsNativeMethods.SetWindowLongPointer(
                    windowHandle,
                    WindowsNativeConstants.WindowLongUserData,
                    statePointer);
            }
            else
            {
                statePointer = WindowsNativeMethods.GetWindowLongPointer(
                    windowHandle,
                    WindowsNativeConstants.WindowLongUserData);
            }

            DelayedRenderingClipboardOwner? owner = statePointer == 0
                ? null
                : GCHandle.FromIntPtr(statePointer).Target as DelayedRenderingClipboardOwner;
            if (owner is not null)
            {
                switch (message)
                {
                    case WindowsNativeConstants.WindowMessageRenderFormat:
                        if ((uint)wParam == WindowsNativeConstants.ClipboardFormatUnicodeText)
                        {
                            // WM_RENDERFORMAT 到达时剪贴板已由消费者打开；owner 不能再次 OpenClipboard。
                            owner.RenderUnicodeText();
                        }

                        return 0;

                    case WindowsNativeConstants.WindowMessageRenderAllFormats:
                        owner.RenderAllFormats(windowHandle);
                        return 0;

                    case WindowsNativeConstants.WindowMessageClose:
                        owner.RenderAllFormats(windowHandle);
                        WindowsNativeMethods.DestroyWindow(windowHandle);
                        return 0;

                    case WindowsNativeConstants.WindowMessageDestroy:
                        WindowsNativeMethods.PostQuitMessage(0);
                        return 0;

                    case WindowsNativeConstants.WindowMessageNonClientDestroy:
                        WindowsNativeMethods.SetWindowLongPointer(
                            windowHandle,
                            WindowsNativeConstants.WindowLongUserData,
                            0);
                        owner.ClearWindowHandle();
                        break;
                }
            }
        }
        catch
        {
            // 验证 owner 也必须遵守原生回调边界，异常不能逃逸到 Native AOT WNDPROC。
        }

        return WindowsNativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }
}
