namespace SnapBoard.Platform.MacOS.Interop;

internal static class MacOSAppKit
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            if (MacOSNativeMethods.NSApplicationLoad() == 0)
            {
                throw new InvalidOperationException("AppKit initialization failed.");
            }

            Volatile.Write(ref _initialized, true);
        }
    }
}
