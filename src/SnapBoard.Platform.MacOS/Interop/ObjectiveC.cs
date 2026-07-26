using System.Runtime.InteropServices;
using System.Text;

namespace SnapBoard.Platform.MacOS.Interop;

internal static class ObjectiveC
{
    private const nuint Utf8StringEncoding = 4;
    private static readonly nint AllocSelector = GetSelector("alloc");
    private static readonly nint BytesSelector = GetSelector("bytes");
    private static readonly nint DataWithBytesSelector = GetSelector("dataWithBytes:length:");
    private static readonly nint InitWithBytesSelector = GetSelector("initWithBytes:length:encoding:");
    private static readonly nint LengthSelector = GetSelector("length");
    private static readonly nint ReleaseSelector = GetSelector("release");
    private static readonly nint Utf8StringSelector = GetSelector("UTF8String");

    private static readonly nint DataClass = GetRequiredClass("NSData");
    private static readonly nint StringClass = GetRequiredClass("NSString");

    public static nint GetRequiredClass(string name)
    {
        nint value = MacOSNativeMethods.GetClass(name);
        return value != 0
            ? value
            : throw new InvalidOperationException($"Objective-C class '{name}' is unavailable.");
    }

    public static nint GetSelector(string name)
    {
        nint value = MacOSNativeMethods.RegisterSelector(name);
        return value != 0
            ? value
            : throw new InvalidOperationException($"Objective-C selector '{name}' is unavailable.");
    }

    public static unsafe nint CreateString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        nint allocated = MacOSNativeMethods.SendIntPtr(StringClass, AllocSelector);
        if (allocated == 0)
        {
            return 0;
        }

        fixed (byte* bytesPointer = bytes)
        {
            return MacOSNativeMethods.SendIntPtrWithIntPtrNUIntNUInt(
                allocated,
                InitWithBytesSelector,
                (nint)bytesPointer,
                (nuint)bytes.Length,
                Utf8StringEncoding);
        }
    }

    public static string? ToManagedString(nint nativeString)
    {
        if (nativeString == 0)
        {
            return null;
        }

        nint utf8 = MacOSNativeMethods.SendIntPtr(nativeString, Utf8StringSelector);
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    public static unsafe nint CreateData(ReadOnlySpan<byte> data)
    {
        fixed (byte* dataPointer = data)
        {
            return MacOSNativeMethods.SendIntPtrWithIntPtrNUInt(
                DataClass,
                DataWithBytesSelector,
                (nint)dataPointer,
                (nuint)data.Length);
        }
    }

    public static byte[]? ToManagedData(nint nativeData, int maximumBytes)
    {
        if (nativeData == 0)
        {
            return null;
        }

        nuint nativeLength = MacOSNativeMethods.SendNUInt(nativeData, LengthSelector);
        if (nativeLength > (nuint)maximumBytes || nativeLength > int.MaxValue)
        {
            return null;
        }

        int length = (int)nativeLength;
        if (length == 0)
        {
            return [];
        }

        nint bytes = MacOSNativeMethods.SendIntPtr(nativeData, BytesSelector);
        if (bytes == 0)
        {
            return null;
        }

        byte[] managed = new byte[length];
        Marshal.Copy(bytes, managed, 0, length);
        return managed;
    }

    public static long GetDataLength(nint nativeData)
    {
        if (nativeData == 0)
        {
            return -1;
        }

        nuint nativeLength = MacOSNativeMethods.SendNUInt(nativeData, LengthSelector);
        return nativeLength > long.MaxValue ? long.MaxValue : (long)nativeLength;
    }

    public static void Release(nint value)
    {
        if (value != 0)
        {
            MacOSNativeMethods.SendVoid(value, ReleaseSelector);
        }
    }
}

internal readonly ref struct NativeAutoreleasePool
{
    private readonly nint _token;

    public NativeAutoreleasePool()
    {
        _token = MacOSNativeMethods.PushAutoreleasePool();
    }

    public void Dispose()
    {
        if (_token != 0)
        {
            MacOSNativeMethods.PopAutoreleasePool(_token);
        }
    }
}
