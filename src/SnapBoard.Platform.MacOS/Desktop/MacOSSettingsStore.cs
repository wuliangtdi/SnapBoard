using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

internal interface IMacOSSettingsStore : IDisposable
{
    string? GetString(string key);

    void SetString(string key, string value);
}

internal sealed class MacOSSettingsStore : IMacOSSettingsStore
{
    private readonly object _gate = new();
    private readonly nint _defaults;
    private readonly nint _setObjectForKeySelector;
    private readonly nint _stringForKeySelector;
    private readonly nint _synchronizeSelector;
    private int _disposed;

    public MacOSSettingsStore()
    {
        MacOSAppKit.EnsureInitialized();
        nint defaultsClass = ObjectiveC.GetRequiredClass("NSUserDefaults");
        _defaults = MacOSNativeMethods.SendIntPtr(
            defaultsClass,
            ObjectiveC.GetSelector("standardUserDefaults"));

        if (_defaults == 0)
        {
            throw new InvalidOperationException("NSUserDefaults initialization failed.");
        }

        // standardUserDefaults 由系统共享；服务持有期间显式保留，退出时对称释放。
        MacOSNativeMethods.SendIntPtr(_defaults, ObjectiveC.GetSelector("retain"));

        _setObjectForKeySelector = ObjectiveC.GetSelector("setObject:forKey:");
        _stringForKeySelector = ObjectiveC.GetSelector("stringForKey:");
        _synchronizeSelector = ObjectiveC.GetSelector("synchronize");
    }

    public string? GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            using NativeAutoreleasePool pool = new();
            nint nativeKey = ObjectiveC.CreateString(key);
            try
            {
                return ObjectiveC.ToManagedString(MacOSNativeMethods.SendIntPtrWithIntPtr(
                    _defaults,
                    _stringForKeySelector,
                    nativeKey));
            }
            finally
            {
                ObjectiveC.Release(nativeKey);
            }
        }
    }

    public void SetString(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            using NativeAutoreleasePool pool = new();
            nint nativeKey = ObjectiveC.CreateString(key);
            nint nativeValue = ObjectiveC.CreateString(value);
            try
            {
                MacOSNativeMethods.SendVoidWithIntPtrIntPtr(
                    _defaults,
                    _setObjectForKeySelector,
                    nativeValue,
                    nativeKey);
                MacOSNativeMethods.SendBool(_defaults, _synchronizeSelector);
            }
            finally
            {
                ObjectiveC.Release(nativeValue);
                ObjectiveC.Release(nativeKey);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ObjectiveC.Release(_defaults);
            }
        }
    }
}
