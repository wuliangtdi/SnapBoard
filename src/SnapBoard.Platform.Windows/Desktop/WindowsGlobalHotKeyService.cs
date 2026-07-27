using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Desktop;

[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotKeyService : IGlobalHotKeyService, IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<HotKeyCommand> _commands = new();
    private readonly Channel<bool> _pressedEvents = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly WindowsHotKeyRegistrar _registrar;
    private readonly IWindowsRegistryStore _registry;
    private readonly string _windowClassName = $"SnapBoard.HotKey.{Guid.NewGuid():N}";
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private Task? _pressedPump;
    private nint _windowHandle;
    private uint _threadId;
    private bool _stopRequested;
    private int _state;
    private GlobalHotKeyGesture _configuredGesture;

    private const string SettingsSubKey = @"Software\SnapBoard\Desktop";
    private const string HotKeyValueName = "GlobalHotKey";

    public WindowsGlobalHotKeyService()
        : this(
            new WindowsHotKeyRegistrar(new WindowsHotKeyNative()),
            new WindowsRegistryStore())
    {
    }

    internal WindowsGlobalHotKeyService(
        WindowsHotKeyRegistrar registrar,
        IWindowsRegistryStore registry)
    {
        _registrar = registrar;
        _registry = registry;
        _configuredGesture = ReadConfiguredGesture();
    }

    public event EventHandler? Pressed;

    public GlobalHotKeyGesture? CurrentGesture => _registrar.CurrentGesture;

    public GlobalHotKeyGesture ConfiguredGesture
    {
        get
        {
            lock (_gate)
            {
                return _configuredGesture;
            }
        }
    }

    public GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeyModifiers modifiers,
        string keyName) => WindowsHotKeyKeyMap.CreateGesture(modifiers, keyName);

    public async ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken)
    {
        if (gesture.VirtualKey is 0 or > 0xFE ||
            (gesture.Modifiers & ~KnownModifiers) != 0)
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Failed);
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<GlobalHotKeyRegistrationResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(new HotKeyCommand(gesture, completion, cancellationToken));
        PostCommandMessage(completion);
        GlobalHotKeyRegistrationResult result =
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status == GlobalHotKeyRegistrationStatus.Registered &&
            _registrar.CurrentGesture == gesture)
        {
            lock (_gate)
            {
                _configuredGesture = gesture;
            }

            try
            {
                _registry.SetString(SettingsSubKey, HotKeyValueName, SerializeGesture(gesture));
            }
            catch (Exception exception) when (IsRegistryFailure(exception))
            {
                // 快捷键已经在当前会话注册成功；配置写入失败只影响下次启动，不回滚有效绑定。
            }
        }

        return result;
    }

    public async ValueTask UnregisterAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) is 0 or 4)
        {
            return;
        }

        TaskCompletionSource<GlobalHotKeyRegistrationResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(new HotKeyCommand(null, completion, cancellationToken));
        PostCommandMessage(completion);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Task stoppedTask;
        nint windowHandle;
        uint threadId;

        lock (_gate)
        {
            if (_state == 0)
            {
                _state = 4;
                _started.TrySetException(new ObjectDisposedException(nameof(WindowsGlobalHotKeyService)));
                _stopped.TrySetResult();
                _pressedEvents.Writer.TryComplete();
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
                _state = 3;
                stoppedTask = _stopped.Task;
                windowHandle = _windowHandle;
                threadId = _threadId;
            }
        }

        // 句柄销毁和 UnregisterHotKey 必须回到创建 HWND 的 STA 线程；调用线程只投递退出消息。
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
        _pressedEvents.Writer.TryComplete();
        if (_pressedPump is not null)
        {
            await _pressedPump.ConfigureAwait(false);
        }
    }

    private const GlobalHotKeyModifiers KnownModifiers =
        GlobalHotKeyModifiers.Alt |
        GlobalHotKeyModifiers.Control |
        GlobalHotKeyModifiers.Shift |
        GlobalHotKeyModifiers.Windows |
        GlobalHotKeyModifiers.NoRepeat;

    private async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        Task startedTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == 4, this);
            if (_state == 0)
            {
                _state = 1;
                _pressedPump = Task.Run(PumpPressedEventsAsync, CancellationToken.None);
                _thread = new Thread(RunMessageLoop)
                {
                    IsBackground = true,
                    Name = "SnapBoard Windows global hotkey",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            startedTask = _started.Task;
        }

        await startedTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PostCommandMessage(
        TaskCompletionSource<GlobalHotKeyRegistrationResult> completion)
    {
        nint windowHandle;
        lock (_gate)
        {
            windowHandle = _windowHandle;
        }

        if (windowHandle == 0 || !WindowsNativeMethods.PostMessage(
                windowHandle,
                WindowsNativeConstants.WindowMessageHotKeyCommand,
                0,
                0))
        {
            completion.TrySetException(new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private async Task PumpPressedEventsAsync()
    {
        await foreach (bool _ in _pressedEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // 用户回调在消息线程之外执行；单个订阅者失败不影响后续快捷键事件。
            }
        }
    }

    private void RunMessageLoop()
    {
        GCHandle selfHandle = default;
        ushort classAtom = 0;
        nint instance = 0;

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

            bool shouldStop;
            lock (_gate)
            {
                _windowHandle = windowHandle;
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
            _started.TrySetException(exception);
            FailPendingCommands(exception);
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
                _registrar.Unregister(windowHandle);
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

            FailPendingCommands(new ObjectDisposedException(nameof(WindowsGlobalHotKeyService)));
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

    private void ProcessCommands(nint windowHandle)
    {
        while (_commands.TryDequeue(out HotKeyCommand? command))
        {
            if (command.CancellationToken.IsCancellationRequested)
            {
                command.Completion.TrySetCanceled(command.CancellationToken);
                continue;
            }

            if (command.Gesture is GlobalHotKeyGesture gesture)
            {
                command.Completion.TrySetResult(_registrar.Register(windowHandle, gesture));
            }
            else
            {
                _registrar.Unregister(windowHandle);
                command.Completion.TrySetResult(
                    new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Registered));
            }
        }
    }

    private void FailPendingCommands(Exception exception)
    {
        while (_commands.TryDequeue(out HotKeyCommand? command))
        {
            command.Completion.TrySetException(exception);
        }
    }

    private void ClearWindowHandle()
    {
        lock (_gate)
        {
            _windowHandle = 0;
        }
    }

    private GlobalHotKeyGesture ReadConfiguredGesture()
    {
        try
        {
            string? value = _registry.GetString(SettingsSubKey, HotKeyValueName);
            string[] parts = value?.Split('|', 3, StringSplitOptions.TrimEntries) ?? [];
            if (parts.Length == 3 &&
                uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint modifiers) &&
                uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint virtualKey) &&
                virtualKey is > 0 and <= 0xFE &&
                (((GlobalHotKeyModifiers)modifiers) & ~KnownModifiers) == 0 &&
                !string.IsNullOrWhiteSpace(parts[2]))
            {
                return new GlobalHotKeyGesture(
                    (GlobalHotKeyModifiers)modifiers,
                    virtualKey,
                    parts[2]);
            }
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
        }

        return GlobalHotKeyGesture.Default;
    }

    private static string SerializeGesture(GlobalHotKeyGesture gesture) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(uint)gesture.Modifiers}|{gesture.VirtualKey}|{gesture.DisplayName}");

    private static bool IsRegistryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

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

            WindowsGlobalHotKeyService? host = statePointer == 0
                ? null
                : GCHandle.FromIntPtr(statePointer).Target as WindowsGlobalHotKeyService;
            if (host is not null)
            {
                switch (message)
                {
                    case WindowsNativeConstants.WindowMessageHotKey:
                        // 原生回调只尝试写入容量为 1 的 Channel；窗口显示、焦点和业务逻辑
                        // 全部由后台事件泵转交给 Avalonia Dispatcher，禁止在 WNDPROC 中执行。
                        host._pressedEvents.Writer.TryWrite(true);
                        return 0;

                    case WindowsNativeConstants.WindowMessageHotKeyCommand:
                        host.ProcessCommands(windowHandle);
                        return 0;

                    case WindowsNativeConstants.WindowMessageClose:
                        host._registrar.Unregister(windowHandle);
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
            // 托管异常不得穿过 Native AOT 的 WNDPROC 边界。
        }

        return WindowsNativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private sealed record HotKeyCommand(
        GlobalHotKeyGesture? Gesture,
        TaskCompletionSource<GlobalHotKeyRegistrationResult> Completion,
        CancellationToken CancellationToken);
}
