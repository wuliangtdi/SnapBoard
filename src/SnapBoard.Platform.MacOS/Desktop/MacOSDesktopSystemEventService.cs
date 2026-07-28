using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

/// <summary>
/// 通过 NSWorkspace 与 SystemConfiguration 接收真实系统唤醒和网络状态变化。
/// 原生回调只发布信号，不在平台层执行同步或访问业务状态。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSDesktopSystemEventService : IDesktopSystemEventService
{
    private const string CallbackClassName = "SnapBoardSystemEventTarget";
    private const string NetworkPattern = "State:/Network/Global/.*";
    private const uint Utf8StringEncoding = 0x08000100;

    private static readonly object CallbackClassGate = new();
    private static readonly ConcurrentDictionary<
        nint,
        WeakReference<MacOSDesktopSystemEventService>> NetworkHosts = new();
    private static readonly ConcurrentDictionary<
        nint,
        WeakReference<MacOSDesktopSystemEventService>> WakeHosts = new();
    private static nint _callbackClass;

    private readonly IPlatformMainThreadDispatcher _dispatcher;
    private nint _dynamicStore;
    private nint _networkPattern;
    private nint _networkPatterns;
    private nint _notificationCenter;
    private nint _wakeNotificationName;
    private nint _wakeTarget;
    private int _dispatchQueueAttached;
    private int _disposed;
    private int _started;

    public MacOSDesktopSystemEventService(IPlatformMainThreadDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public event EventHandler? SystemResumed;

    public event EventHandler? NetworkChanged;

    internal bool IsWakeObservationActive => Volatile.Read(ref _wakeTarget) != 0;

    internal bool IsNetworkObservationActive => Volatile.Read(ref _dynamicStore) != 0 &&
        Volatile.Read(ref _dispatchQueueAttached) != 0;

    internal unsafe void InvokeNetworkCallbackProbe()
    {
        delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback = &NetworkStoreChanged;
        callback(Volatile.Read(ref _dynamicStore), 0, 0);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _dispatcher.Invoke(() =>
            {
                InitializeOnMainThread();
                return true;
            });
        }
        catch
        {
            try
            {
                _dispatcher.Invoke(() =>
                {
                    DisposeOnMainThread();
                    return true;
                });
                Interlocked.Exchange(ref _started, 0);
            }
            catch
            {
                // 保留 started 状态，使应用退出时的 Dispose 能再次尝试释放原生句柄。
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _started) != 0)
        {
            _dispatcher.Invoke(() =>
            {
                DisposeOnMainThread();
                return true;
            });
        }

        Interlocked.Exchange(ref _started, 0);
        GC.SuppressFinalize(this);
    }

    private void InitializeOnMainThread()
    {
        MacOSAppKit.EnsureInitialized();
        using NativeAutoreleasePool pool = new();
        InitializeWakeObservation();
        InitializeNetworkObservation();
    }

    private void InitializeWakeObservation()
    {
        nint callbackClass = GetOrCreateCallbackClass();
        _wakeTarget = MacOSNativeMethods.SendIntPtr(
            MacOSNativeMethods.SendIntPtr(callbackClass, ObjectiveC.GetSelector("alloc")),
            ObjectiveC.GetSelector("init"));
        if (_wakeTarget == 0)
        {
            throw new InvalidOperationException(
                "macOS system event callback target initialization failed.");
        }

        WakeHosts[_wakeTarget] = new WeakReference<MacOSDesktopSystemEventService>(this);
        nint workspace = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSWorkspace"),
            ObjectiveC.GetSelector("sharedWorkspace"));
        _notificationCenter = MacOSNativeMethods.SendIntPtr(
            workspace,
            ObjectiveC.GetSelector("notificationCenter"));
        _wakeNotificationName = ObjectiveC.CreateString("NSWorkspaceDidWakeNotification");
        if (_notificationCenter == 0 || _wakeNotificationName == 0)
        {
            throw new InvalidOperationException(
                "macOS workspace wake notification center is unavailable.");
        }

        MacOSNativeMethods.SendVoidWithFourIntPtr(
            _notificationCenter,
            ObjectiveC.GetSelector("addObserver:selector:name:object:"),
            _wakeTarget,
            ObjectiveC.GetSelector("snapBoardSystemDidWake:"),
            _wakeNotificationName,
            0);
    }

    private unsafe void InitializeNetworkObservation()
    {
        nint sessionName = MacOSNativeMethods.CFStringCreateWithCString(
            0,
            "SnapBoard system event monitor",
            Utf8StringEncoding);
        if (sessionName == 0)
        {
            throw new InvalidOperationException(
                "macOS network monitor session name initialization failed.");
        }

        try
        {
            _dynamicStore = MacOSNativeMethods.SCDynamicStoreCreate(
                0,
                sessionName,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&NetworkStoreChanged,
                0);
        }
        finally
        {
            MacOSNativeMethods.CFRelease(sessionName);
        }

        if (_dynamicStore == 0)
        {
            throw new InvalidOperationException(
                "macOS network dynamic store initialization failed.");
        }

        NetworkHosts[_dynamicStore] = new WeakReference<MacOSDesktopSystemEventService>(this);
        _networkPattern = MacOSNativeMethods.CFStringCreateWithCString(
            0,
            NetworkPattern,
            Utf8StringEncoding);
        if (_networkPattern == 0)
        {
            throw new InvalidOperationException(
                "macOS network notification pattern initialization failed.");
        }

        nint pattern = _networkPattern;
        _networkPatterns = MacOSNativeMethods.CFArrayCreate(
            0,
            (nint)(&pattern),
            1,
            0);
        if (_networkPatterns == 0 ||
            MacOSNativeMethods.SCDynamicStoreSetNotificationKeys(
                _dynamicStore,
                0,
                _networkPatterns) == 0)
        {
            throw new InvalidOperationException(
                "macOS network notification registration failed.");
        }

        nint queue = MacOSNativeMethods.DispatchGetGlobalQueue(0, 0);
        if (queue == 0 ||
            MacOSNativeMethods.SCDynamicStoreSetDispatchQueue(_dynamicStore, queue) == 0)
        {
            throw new InvalidOperationException(
                "macOS network notification dispatch queue registration failed.");
        }

        Volatile.Write(ref _dispatchQueueAttached, 1);
    }

    private void DisposeOnMainThread()
    {
        nint dynamicStore = _dynamicStore;
        _dynamicStore = 0;
        if (dynamicStore != 0)
        {
            NetworkHosts.TryRemove(dynamicStore, out _);
            if (Interlocked.Exchange(ref _dispatchQueueAttached, 0) != 0)
            {
                _ = MacOSNativeMethods.SCDynamicStoreSetDispatchQueue(dynamicStore, 0);
            }

            MacOSNativeMethods.CFRelease(dynamicStore);
        }

        MacOSNativeMethods.CFRelease(_networkPatterns);
        _networkPatterns = 0;
        MacOSNativeMethods.CFRelease(_networkPattern);
        _networkPattern = 0;

        nint wakeTarget = _wakeTarget;
        _wakeTarget = 0;
        if (_notificationCenter != 0 && wakeTarget != 0)
        {
            MacOSNativeMethods.SendVoidWithIntPtr(
                _notificationCenter,
                ObjectiveC.GetSelector("removeObserver:"),
                wakeTarget);
        }

        if (wakeTarget != 0)
        {
            WakeHosts.TryRemove(wakeTarget, out _);
            ObjectiveC.Release(wakeTarget);
        }

        _notificationCenter = 0;
        ObjectiveC.Release(_wakeNotificationName);
        _wakeNotificationName = 0;
    }

    private static nint GetOrCreateCallbackClass()
    {
        if (Volatile.Read(ref _callbackClass) != 0)
        {
            return _callbackClass;
        }

        lock (CallbackClassGate)
        {
            if (_callbackClass != 0)
            {
                return _callbackClass;
            }

            nint existing = MacOSNativeMethods.GetClass(CallbackClassName);
            if (existing != 0)
            {
                _callbackClass = existing;
                return existing;
            }

            nint type = MacOSNativeMethods.AllocateClassPair(
                ObjectiveC.GetRequiredClass("NSObject"),
                CallbackClassName,
                0);
            if (type == 0)
            {
                throw new InvalidOperationException(
                    "Objective-C system event callback class allocation failed.");
            }

            unsafe
            {
                if (MacOSNativeMethods.ClassAddMethod(
                        type,
                        ObjectiveC.GetSelector("snapBoardSystemDidWake:"),
                        (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&SystemDidWake,
                        "v@:@") == 0)
                {
                    throw new InvalidOperationException(
                        "Objective-C system event callback method registration failed.");
                }
            }

            MacOSNativeMethods.RegisterClassPair(type);
            Volatile.Write(ref _callbackClass, type);
            return type;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SystemDidWake(nint self, nint _, nint notification)
    {
        _ = notification;
        try
        {
            if (WakeHosts.TryGetValue(
                    self,
                    out WeakReference<MacOSDesktopSystemEventService>? reference) &&
                reference.TryGetTarget(out MacOSDesktopSystemEventService? service))
            {
                service.SystemResumed?.Invoke(service, EventArgs.Empty);
            }
        }
        catch
        {
            // 托管异常不能跨越 Objective-C notification 回调边界。
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NetworkStoreChanged(nint store, nint changedKeys, nint info)
    {
        _ = changedKeys;
        _ = info;
        try
        {
            if (NetworkHosts.TryGetValue(
                    store,
                    out WeakReference<MacOSDesktopSystemEventService>? reference) &&
                reference.TryGetTarget(out MacOSDesktopSystemEventService? service))
            {
                service.NetworkChanged?.Invoke(service, EventArgs.Empty);
            }
        }
        catch
        {
            // 托管异常不能跨越 SystemConfiguration 回调边界。
        }
    }
}
