using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

[SupportedOSPlatform("macos")]
public sealed class MacOSMenuBarService : IDesktopMenuBarService
{
    private const string CallbackClassName = "SnapBoardMenuBarTarget";
    private const double SquareStatusItemLength = -2d;
    private const int MenuStateOff = 0;
    private const int MenuStateOn = 1;
    private const int CommandShowMain = 1;
    private const int CommandShowQuick = 2;
    private const int CommandTogglePause = 3;
    private const int CommandShowSettings = 4;
    private const int CommandExit = 5;

    private static readonly object CallbackClassGate = new();
    private static readonly ConcurrentDictionary<nint, WeakReference<MacOSMenuBarService>> Hosts = new();
    private static nint _callbackClass;

    private readonly IPlatformMainThreadDispatcher _dispatcher;
    private nint _menu;
    private nint _pauseItem;
    private nint _statusBar;
    private nint _statusItem;
    private nint _target;
    private int _initialized;
    private int _disposed;

    public MacOSMenuBarService(IPlatformMainThreadDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event EventHandler? ShowMainWindowRequested;

    public event EventHandler? ShowQuickWindowRequested;

    public event EventHandler? RecordingPauseToggleRequested;

    public event EventHandler? ShowSettingsWindowRequested;

    public event EventHandler? ExitRequested;

    public void Initialize(bool recordingPaused)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            SetRecordingPaused(recordingPaused);
            return;
        }

        try
        {
            _dispatcher.Invoke(() =>
            {
                InitializeOnMainThread(recordingPaused);
                return true;
            });
        }
        catch
        {
            _dispatcher.Invoke(() =>
            {
                DisposeOnMainThread();
                return true;
            });
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    public void SetRecordingPaused(bool paused)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _initialized) == 0)
        {
            return;
        }

        _dispatcher.Invoke(() =>
        {
            SetRecordingPausedOnMainThread(paused);
            return true;
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _initialized) != 0)
        {
            _dispatcher.Invoke(() =>
            {
                DisposeOnMainThread();
                return true;
            });
        }
    }

    private void InitializeOnMainThread(bool recordingPaused)
    {
        MacOSAppKit.EnsureInitialized();
        using NativeAutoreleasePool pool = new();
        nint callbackClass = GetOrCreateCallbackClass();
        _target = MacOSNativeMethods.SendIntPtr(
            MacOSNativeMethods.SendIntPtr(callbackClass, ObjectiveC.GetSelector("alloc")),
            ObjectiveC.GetSelector("init"));
        if (_target == 0)
        {
            throw new InvalidOperationException("macOS menu callback target initialization failed.");
        }

        Hosts[_target] = new WeakReference<MacOSMenuBarService>(this);
        nint menuTitle = ObjectiveC.CreateString(MacOSApplicationIdentity.ProductName);
        try
        {
            _menu = MacOSNativeMethods.SendIntPtrWithIntPtr(
                MacOSNativeMethods.SendIntPtr(
                    ObjectiveC.GetRequiredClass("NSMenu"),
                    ObjectiveC.GetSelector("alloc")),
                ObjectiveC.GetSelector("initWithTitle:"),
                menuTitle);
        }
        finally
        {
            ObjectiveC.Release(menuTitle);
        }

        AddMenuItem("打开闪剪", CommandShowMain);
        AddMenuItem("快速粘贴", CommandShowQuick);
        _pauseItem = AddMenuItem("暂停记录", CommandTogglePause);
        AddMenuItem("设置...", CommandShowSettings);
        nint separator = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSMenuItem"),
            ObjectiveC.GetSelector("separatorItem"));
        MacOSNativeMethods.SendVoidWithIntPtr(
            _menu,
            ObjectiveC.GetSelector("addItem:"),
            separator);
        AddMenuItem("退出闪剪", CommandExit);

        _statusBar = MacOSNativeMethods.SendIntPtr(
            ObjectiveC.GetRequiredClass("NSStatusBar"),
            ObjectiveC.GetSelector("systemStatusBar"));
        nint statusItem = MacOSNativeMethods.SendIntPtrWithDouble(
            _statusBar,
            ObjectiveC.GetSelector("statusItemWithLength:"),
            SquareStatusItemLength);
        if (statusItem == 0)
        {
            throw new InvalidOperationException("NSStatusItem initialization failed.");
        }

        // NSStatusBar 不为调用方持有状态项；托管 nint 也不会形成 Objective-C 强引用。
        // 必须跨越当前 autorelease pool 显式保留，并在 removeStatusItem: 之后配对释放。
        _statusItem = MacOSNativeMethods.SendIntPtr(
            statusItem,
            ObjectiveC.GetSelector("retain"));
        ConfigureStatusButton();
        MacOSNativeMethods.SendVoidWithIntPtr(
            _statusItem,
            ObjectiveC.GetSelector("setMenu:"),
            _menu);
        SetApplicationIcon();
        MacOSApplicationIdentity.SetApplicationMenuTitle();
        SetRecordingPausedOnMainThread(recordingPaused);
    }

    private nint AddMenuItem(string title, int command)
    {
        nint nativeTitle = ObjectiveC.CreateString(title);
        nint emptyKey = ObjectiveC.CreateString(string.Empty);
        try
        {
            nint item = MacOSNativeMethods.SendIntPtrWithIntPtrIntPtrIntPtr(
                MacOSNativeMethods.SendIntPtr(
                    ObjectiveC.GetRequiredClass("NSMenuItem"),
                    ObjectiveC.GetSelector("alloc")),
                ObjectiveC.GetSelector("initWithTitle:action:keyEquivalent:"),
                nativeTitle,
                ObjectiveC.GetSelector("snapBoardMenuItemInvoked:"),
                emptyKey);
            MacOSNativeMethods.SendVoidWithIntPtr(
                item,
                ObjectiveC.GetSelector("setTarget:"),
                _target);
            MacOSNativeMethods.SendVoidWithInt32(
                item,
                ObjectiveC.GetSelector("setTag:"),
                command);
            MacOSNativeMethods.SendVoidWithIntPtr(
                _menu,
                ObjectiveC.GetSelector("addItem:"),
                item);
            ObjectiveC.Release(item);
            return item;
        }
        finally
        {
            ObjectiveC.Release(emptyKey);
            ObjectiveC.Release(nativeTitle);
        }
    }

    private void ConfigureStatusButton()
    {
        nint button = MacOSNativeMethods.SendIntPtr(
            _statusItem,
            ObjectiveC.GetSelector("button"));
        string? imagePath = FindAsset("snapboard-menubar-template.png");
        if (button == 0 || imagePath is null)
        {
            throw new FileNotFoundException("The macOS menu bar template icon is unavailable.");
        }

        nint nativePath = ObjectiveC.CreateString(imagePath);
        nint image = 0;
        try
        {
            image = MacOSNativeMethods.SendIntPtrWithIntPtr(
                MacOSNativeMethods.SendIntPtr(
                    ObjectiveC.GetRequiredClass("NSImage"),
                    ObjectiveC.GetSelector("alloc")),
                ObjectiveC.GetSelector("initWithContentsOfFile:"),
                nativePath);
            if (image == 0)
            {
                throw new InvalidOperationException("The macOS menu bar template icon could not be decoded.");
            }

            MacOSNativeMethods.SendVoidWithByte(
                image,
                ObjectiveC.GetSelector("setTemplate:"),
                1);
            MacOSNativeMethods.SendVoidWithNativeSize(
                image,
                ObjectiveC.GetSelector("setSize:"),
                new NativeSize(18d, 18d));
            MacOSNativeMethods.SendVoidWithIntPtr(
                button,
                ObjectiveC.GetSelector("setImage:"),
                image);

            nint toolTip = ObjectiveC.CreateString(MacOSApplicationIdentity.ProductName);
            try
            {
                MacOSNativeMethods.SendVoidWithIntPtr(
                    button,
                    ObjectiveC.GetSelector("setToolTip:"),
                    toolTip);
                MacOSNativeMethods.SendVoidWithIntPtr(
                    button,
                    ObjectiveC.GetSelector("setAccessibilityLabel:"),
                    toolTip);
            }
            finally
            {
                ObjectiveC.Release(toolTip);
            }
        }
        finally
        {
            ObjectiveC.Release(image);
            ObjectiveC.Release(nativePath);
        }
    }

    private static void SetApplicationIcon()
    {
        string? imagePath = FindAsset("snapboard-app-icon.png");
        if (imagePath is null)
        {
            return;
        }

        nint nativePath = ObjectiveC.CreateString(imagePath);
        nint image = 0;
        try
        {
            image = MacOSNativeMethods.SendIntPtrWithIntPtr(
                MacOSNativeMethods.SendIntPtr(
                    ObjectiveC.GetRequiredClass("NSImage"),
                    ObjectiveC.GetSelector("alloc")),
                ObjectiveC.GetSelector("initWithContentsOfFile:"),
                nativePath);
            nint application = MacOSNativeMethods.SendIntPtr(
                ObjectiveC.GetRequiredClass("NSApplication"),
                ObjectiveC.GetSelector("sharedApplication"));
            if (image != 0 && application != 0)
            {
                MacOSNativeMethods.SendVoidWithIntPtr(
                    application,
                    ObjectiveC.GetSelector("setApplicationIconImage:"),
                    image);
            }
        }
        finally
        {
            ObjectiveC.Release(image);
            ObjectiveC.Release(nativePath);
        }
    }

    private void SetRecordingPausedOnMainThread(bool paused)
    {
        if (_pauseItem == 0)
        {
            return;
        }

        nint title = ObjectiveC.CreateString(paused ? "恢复记录" : "暂停记录");
        try
        {
            MacOSNativeMethods.SendVoidWithIntPtr(
                _pauseItem,
                ObjectiveC.GetSelector("setTitle:"),
                title);
            MacOSNativeMethods.SendVoidWithInt32(
                _pauseItem,
                ObjectiveC.GetSelector("setState:"),
                paused ? MenuStateOn : MenuStateOff);
        }
        finally
        {
            ObjectiveC.Release(title);
        }
    }

    private void DisposeOnMainThread()
    {
        nint statusItem = _statusItem;
        if (_statusBar != 0 && _statusItem != 0)
        {
            MacOSNativeMethods.SendVoidWithIntPtr(
                _statusBar,
                ObjectiveC.GetSelector("removeStatusItem:"),
                _statusItem);
        }

        _statusItem = 0;
        _statusBar = 0;
        ObjectiveC.Release(statusItem);
        _pauseItem = 0;
        ObjectiveC.Release(_menu);
        _menu = 0;
        if (_target != 0)
        {
            Hosts.TryRemove(_target, out _);
            ObjectiveC.Release(_target);
            _target = 0;
        }
    }

    private void InvokeCommand(int command)
    {
        EventHandler? handler = command switch
        {
            CommandShowMain => ShowMainWindowRequested,
            CommandShowQuick => ShowQuickWindowRequested,
            CommandTogglePause => RecordingPauseToggleRequested,
            CommandShowSettings => ShowSettingsWindowRequested,
            CommandExit => ExitRequested,
            _ => null,
        };
        handler?.Invoke(this, EventArgs.Empty);
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
                throw new InvalidOperationException("Objective-C menu callback class allocation failed.");
            }

            unsafe
            {
                if (MacOSNativeMethods.ClassAddMethod(
                        type,
                        ObjectiveC.GetSelector("snapBoardMenuItemInvoked:"),
                        (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&MenuItemInvoked,
                        "v@:@") == 0)
                {
                    throw new InvalidOperationException("Objective-C menu callback method registration failed.");
                }
            }

            MacOSNativeMethods.RegisterClassPair(type);
            Volatile.Write(ref _callbackClass, type);
            return type;
        }
    }

    private static string? FindAsset(string fileName)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "Resources",
                fileName),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuItemInvoked(nint self, nint _, nint sender)
    {
        try
        {
            if (Hosts.TryGetValue(self, out WeakReference<MacOSMenuBarService>? reference) &&
                reference.TryGetTarget(out MacOSMenuBarService? service))
            {
                int command = MacOSNativeMethods.SendInt32(
                    sender,
                    ObjectiveC.GetSelector("tag"));
                service.InvokeCommand(command);
            }
        }
        catch
        {
            // 托管异常不能跨越 Objective-C action 回调边界。
        }
    }
}
