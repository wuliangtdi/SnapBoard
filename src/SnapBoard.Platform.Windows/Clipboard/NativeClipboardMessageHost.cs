using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Clipboard;

[SupportedOSPlatform("windows")]
internal sealed class NativeClipboardMessageHost : IClipboardMessageHost
{
    private readonly object _gate = new();
    private readonly string _windowClassName = $"SnapBoard.Clipboard.{Guid.NewGuid():N}";
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private nint _windowHandle;
    private uint _threadId;
    private bool _listenerRegistered;
    private bool _stopRequested;
    private int _state;

    public event Action<ClipboardUpdateObservation>? ClipboardUpdated;

    public event Action<Exception?>? MessageLoopStopped;

    public nint WindowHandle
    {
        get
        {
            lock (_gate)
            {
                return _windowHandle;
            }
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        Task startedTask;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == 4, this);

            if (_state == 0)
            {
                _state = 1;
                _thread = new Thread(RunMessageLoop)
                {
                    IsBackground = true,
                    Name = "SnapBoard Windows clipboard listener",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            startedTask = _started.Task;
        }

        await startedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StopAsync()
    {
        Task stoppedTask;
        nint windowHandle;
        uint threadId;

        lock (_gate)
        {
            if (_state == 0)
            {
                _state = 4;
                _started.TrySetException(new ObjectDisposedException(nameof(NativeClipboardMessageHost)));
                _stopped.TrySetResult();
                return;
            }

            if (_state == 4)
            {
                stoppedTask = _stopped.Task;
                windowHandle = 0;
                threadId = 0;
            }
            else
            {
                _stopRequested = true;
                stoppedTask = _stopped.Task;
                windowHandle = _windowHandle;
                threadId = _threadId;
                _state = 3;
            }
        }

        // 退出请求只投递消息，不从调用线程销毁 HWND。RemoveClipboardFormatListener、
        // DestroyWindow 和 GetMessage 退出全部留在创建窗口的 STA 线程，避免句柄竞态。
        if (windowHandle != 0)
        {
            WindowsNativeMethods.PostMessage(
                windowHandle,
                WindowsNativeConstants.WindowMessageClose,
                0,
                0);
        }
        else if (threadId != 0)
        {
            WindowsNativeMethods.PostThreadMessage(
                threadId,
                WindowsNativeConstants.WindowMessageQuit,
                0,
                0);
        }

        await stoppedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => StopAsync();

    private void RunMessageLoop()
    {
        GCHandle selfHandle = default;
        ushort classAtom = 0;
        nint instance = 0;
        Exception? failure = null;

        try
        {
            uint threadId = WindowsNativeMethods.GetCurrentThreadId();
            lock (_gate)
            {
                _threadId = threadId;
            }

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
                WindowsNativeConstants.MessageOnlyWindowParent,
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

            if (!WindowsNativeMethods.AddClipboardFormatListener(windowHandle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _listenerRegistered = true;

            bool shouldStop;
            lock (_gate)
            {
                if (_state != 3)
                {
                    _state = 2;
                }

                shouldStop = _stopRequested;
            }

            _started.TrySetResult();

            if (shouldStop)
            {
                WindowsNativeMethods.PostMessage(
                    windowHandle,
                    WindowsNativeConstants.WindowMessageClose,
                    0,
                    0);
            }

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
            failure = exception;
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
                RemoveListener(windowHandle);
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
                _threadId = 0;
                _state = 4;
            }

            try
            {
                MessageLoopStopped?.Invoke(failure);
            }
            catch
            {
                // 内部终止通知也不能逃逸出消息线程；适配器的处理器只负责完成有界队列。
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

    private void HandleClipboardUpdate()
    {
        uint sequenceNumber = WindowsNativeMethods.GetClipboardSequenceNumber();
        if (sequenceNumber != 0)
        {
            // 消息回调只抓 HWND/PID 数值线索；进程、包身份和文件系统查询全部留给后台 reader。
            ClipboardUpdated?.Invoke(new ClipboardUpdateObservation(
                sequenceNumber,
                GetWindowProcessId(WindowsNativeMethods.GetClipboardOwner()),
                GetWindowProcessId(WindowsNativeMethods.GetForegroundWindow())));
        }
    }

    private static int? GetWindowProcessId(nint windowHandle)
    {
        if (windowHandle == 0 ||
            WindowsNativeMethods.GetWindowThreadProcessId(windowHandle, out uint processId) == 0 ||
            processId is 0 or > int.MaxValue)
        {
            return null;
        }

        return (int)processId;
    }

    private void RemoveListener(nint windowHandle)
    {
        if (!_listenerRegistered)
        {
            return;
        }

        WindowsNativeMethods.RemoveClipboardFormatListener(windowHandle);
        _listenerRegistered = false;
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

            NativeClipboardMessageHost? host = statePointer == 0
                ? null
                : GCHandle.FromIntPtr(statePointer).Target as NativeClipboardMessageHost;

            if (host is not null)
            {
                switch (message)
                {
                    case WindowsNativeConstants.WindowMessageClipboardUpdate:
                        // 这里是系统消息回调的硬边界：只读取序列号、窗口 PID 并尝试写入有界队列。
                        // 禁止在此 OpenClipboard、复制大图、访问 SQLite 或等待任何异步操作。
                        host.HandleClipboardUpdate();
                        return 0;

                    case WindowsNativeConstants.WindowMessageClose:
                        host.RemoveListener(windowHandle);
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
                        host.ClearWindowHandle();
                        break;
                }
            }
        }
        catch
        {
            // 任何托管异常都不能穿过原生 WNDPROC 边界，否则 Native AOT 进程可能直接终止。
        }

        return WindowsNativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }
}
