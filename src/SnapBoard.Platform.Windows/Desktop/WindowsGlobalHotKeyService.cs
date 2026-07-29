using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Desktop;

[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotKeyService :
    IGlobalHotKeyService,
    ITwoSlotGlobalHotKeyService,
    IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly ConcurrentQueue<HotKeyCommand> _commands = new();
    private readonly Channel<GlobalHotKeySlot> _pressedEvents =
        Channel.CreateBounded<GlobalHotKeySlot>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly WindowsHotKeyRegistrar _registrar;
    private readonly IDesktopLocalSettingsService _settings;
    private readonly string _windowClassName = $"SnapBoard.HotKey.{Guid.NewGuid():N}";
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private Task? _pressedPump;
    private nint _windowHandle;
    private uint _threadId;
    private bool _stopRequested;
    private int _state;
    private readonly TimeSpan _doubleTriggerInterval;

    public WindowsGlobalHotKeyService()
        : this(
            new WindowsHotKeyRegistrar(new WindowsHotKeyNative()),
            new WindowsDesktopLocalSettingsService(),
            GetSystemDoubleTriggerInterval())
    {
    }

    public WindowsGlobalHotKeyService(WindowsDesktopLocalSettingsService settings)
        : this(
            new WindowsHotKeyRegistrar(new WindowsHotKeyNative()),
            settings,
            GetSystemDoubleTriggerInterval())
    {
    }

    internal WindowsGlobalHotKeyService(
        WindowsHotKeyRegistrar registrar,
        IDesktopLocalSettingsService settings,
        TimeSpan? doubleTriggerInterval = null)
    {
        _registrar = registrar;
        _settings = settings;
        _doubleTriggerInterval = doubleTriggerInterval ?? TimeSpan.FromMilliseconds(400);
    }

    public event EventHandler? Pressed;

    public event EventHandler<GlobalHotKeyTriggeredEventArgs>? Triggered;

    public GlobalHotKeyGesture? CurrentGesture =>
        _registrar.GetCurrentGesture(GlobalHotKeySlot.Primary);

    public GlobalHotKeyGesture ConfiguredGesture => _settings.Current.PrimaryHotKey;

    public GlobalHotKeyGesture DefaultGesture => GlobalHotKeyGesture.WindowsDefault;

    public string ModifierDisplayNames => "Ctrl、Alt、Shift 或 Win";

    public TimeSpan DoubleTriggerInterval => _doubleTriggerInterval;

    public GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeyModifiers modifiers,
        string keyName) => WindowsHotKeyKeyMap.CreateGesture(modifiers, keyName);

    public GlobalHotKeyGestureCreationResult CreateGesture(
        GlobalHotKeySlot slot,
        GlobalHotKeyModifiers modifiers,
        string keyName) => Enum.IsDefined(slot)
        ? WindowsHotKeyKeyMap.CreateGesture(
            modifiers,
            keyName,
            requireModifier: slot == GlobalHotKeySlot.Primary)
        : new GlobalHotKeyGestureCreationResult(
            GlobalHotKeyGestureCreationStatus.UnsupportedKey);

    public async ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken) =>
        await RegisterAsync(GlobalHotKeySlot.Primary, gesture, cancellationToken)
            .ConfigureAwait(false);

    public GlobalHotKeyGesture? GetCurrentGesture(GlobalHotKeySlot slot) =>
        _registrar.GetCurrentGesture(slot);

    public GlobalHotKeyGesture? GetConfiguredGesture(GlobalHotKeySlot slot) => slot switch
    {
        GlobalHotKeySlot.Primary => _settings.Current.PrimaryHotKey,
        GlobalHotKeySlot.Double => _settings.Current.DoubleHotKey,
        _ => null,
    };

    public async ValueTask<GlobalHotKeyRegistrationResult> RegisterAsync(
        GlobalHotKeySlot slot,
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RegisterCoreAsync(slot, gesture, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private async ValueTask<GlobalHotKeyRegistrationResult> RegisterCoreAsync(
        GlobalHotKeySlot slot,
        GlobalHotKeyGesture gesture,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(slot) ||
            !WindowsDesktopLocalSettingsService.IsValidGesture(
                gesture,
                requireModifier: slot == GlobalHotKeySlot.Primary))
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Failed);
        }

        DesktopLocalSettings currentSettings = _settings.Current;
        GlobalHotKeyGesture? otherGesture = slot == GlobalHotKeySlot.Primary
            ? currentSettings.DoubleHotKey
            : currentSettings.PrimaryHotKey;
        if (otherGesture is GlobalHotKeyGesture other && gesture.HasSameBinding(other))
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Duplicate);
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<GlobalHotKeyRegistrationResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(new HotKeyCommand(
            HotKeyCommandType.Register,
            slot,
            gesture,
            completion,
            cancellationToken));
        PostCommandMessage(completion);
        GlobalHotKeyRegistrationResult result =
            await completion.Task.ConfigureAwait(false);
        if (result.Status == GlobalHotKeyRegistrationStatus.Registered &&
            _registrar.GetCurrentGesture(slot) == gesture)
        {
            DesktopLocalSettingsUpdateResult updateResult = _settings.Update(settings =>
                slot == GlobalHotKeySlot.Primary
                    ? settings with { PrimaryHotKey = gesture }
                    : settings with { DoubleHotKey = gesture });
            return result with { SettingsPersisted = updateResult.Persisted };
        }

        return result;
    }

    public async ValueTask<GlobalHotKeyRegistrationResult> ClearAsync(
        GlobalHotKeySlot slot,
        CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ClearCoreAsync(slot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private async ValueTask<GlobalHotKeyRegistrationResult> ClearCoreAsync(
        GlobalHotKeySlot slot,
        CancellationToken cancellationToken)
    {
        if (slot != GlobalHotKeySlot.Double)
        {
            return new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Unsupported);
        }

        GlobalHotKeyRegistrationResult result;
        if (Volatile.Read(ref _state) == 0)
        {
            result = new GlobalHotKeyRegistrationResult(GlobalHotKeyRegistrationStatus.Registered);
        }
        else
        {
            TaskCompletionSource<GlobalHotKeyRegistrationResult> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Enqueue(new HotKeyCommand(
                HotKeyCommandType.ClearSlot,
                slot,
                null,
                completion,
                cancellationToken));
            PostCommandMessage(completion);
            result = await completion.Task.ConfigureAwait(false);
        }

        if (result.Status != GlobalHotKeyRegistrationStatus.Registered)
        {
            return result;
        }

        DesktopLocalSettingsUpdateResult updateResult =
            _settings.Update(settings => settings with { DoubleHotKey = null });
        return result with { SettingsPersisted = updateResult.Persisted };
    }

    public async ValueTask UnregisterAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _state) is 0 or 4)
        {
            return;
        }

        TaskCompletionSource<GlobalHotKeyRegistrationResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(new HotKeyCommand(
            HotKeyCommandType.UnregisterAll,
            GlobalHotKeySlot.Primary,
            null,
            completion,
            cancellationToken));
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
        await foreach (GlobalHotKeySlot source in
            _pressedEvents.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Triggered?.Invoke(this, new GlobalHotKeyTriggeredEventArgs(source));
                if (source == GlobalHotKeySlot.Primary)
                {
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
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
                _registrar.UnregisterAll(windowHandle);
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

            switch (command.Type)
            {
                case HotKeyCommandType.Register when command.Gesture is GlobalHotKeyGesture gesture:
                    command.Completion.TrySetResult(
                        _registrar.Register(windowHandle, command.Slot, gesture));
                    break;
                case HotKeyCommandType.ClearSlot:
                    command.Completion.TrySetResult(
                        _registrar.Clear(windowHandle, command.Slot));
                    break;
                case HotKeyCommandType.UnregisterAll:
                    _registrar.UnregisterAll(windowHandle);
                    command.Completion.TrySetResult(new GlobalHotKeyRegistrationResult(
                        GlobalHotKeyRegistrationStatus.Registered));
                    break;
                default:
                    command.Completion.TrySetResult(new GlobalHotKeyRegistrationResult(
                        GlobalHotKeyRegistrationStatus.Failed));
                    break;
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

    private static TimeSpan GetSystemDoubleTriggerInterval()
    {
        uint milliseconds = WindowsNativeMethods.GetDoubleClickTime();
        if (milliseconds == 0)
        {
            milliseconds = 400;
        }

        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 250u, 700u));
    }

    internal bool QueueActiveHotKeyIdentifier(int identifier)
    {
        if (!_registrar.TryGetActiveSlot(identifier, out GlobalHotKeySlot slot))
        {
            return false;
        }

        return _pressedEvents.Writer.TryWrite(slot);
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

            WindowsGlobalHotKeyService? host = statePointer == 0
                ? null
                : GCHandle.FromIntPtr(statePointer).Target as WindowsGlobalHotKeyService;
            if (host is not null)
            {
                switch (message)
                {
                    case WindowsNativeConstants.WindowMessageHotKey:
                        // 只转发此刻仍活动的注册 ID；清除或替换前已排队的旧消息必须丢弃。
                        host.QueueActiveHotKeyIdentifier((int)wParam);
                        return 0;

                    case WindowsNativeConstants.WindowMessageHotKeyCommand:
                        host.ProcessCommands(windowHandle);
                        return 0;

                    case WindowsNativeConstants.WindowMessageClose:
                        host._registrar.UnregisterAll(windowHandle);
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

    private enum HotKeyCommandType
    {
        Register = 0,
        ClearSlot = 1,
        UnregisterAll = 2,
    }

    private sealed record HotKeyCommand(
        HotKeyCommandType Type,
        GlobalHotKeySlot Slot,
        GlobalHotKeyGesture? Gesture,
        TaskCompletionSource<GlobalHotKeyRegistrationResult> Completion,
        CancellationToken CancellationToken);
}
