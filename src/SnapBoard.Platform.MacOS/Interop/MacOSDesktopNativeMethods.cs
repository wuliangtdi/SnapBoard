using System.Runtime.InteropServices;

namespace SnapBoard.Platform.MacOS.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEventTypeSpec
{
    public uint EventClass;
    public uint EventKind;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeEventHotKeyId
{
    public uint Signature;
    public uint Id;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativePoint(double X, double Y);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeSize(double Width, double Height);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRectangle(
    NativePoint Origin,
    NativeSize Size);

internal static partial class MacOSNativeMethods
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(LibSystem, EntryPoint = "flock", SetLastError = true)]
    internal static partial int Flock(int fileDescriptor, int operation);

    [LibraryImport(
        ObjectiveCRuntime,
        EntryPoint = "objc_allocateClassPair",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint AllocateClassPair(
        nint superclass,
        string name,
        nuint extraBytes);

    [LibraryImport(
        ObjectiveCRuntime,
        EntryPoint = "class_addMethod",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial byte ClassAddMethod(
        nint type,
        nint selector,
        nint implementation,
        string typeEncoding);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_registerClassPair")]
    internal static partial void RegisterClassPair(nint type);

    [LibraryImport(Carbon, EntryPoint = "GetApplicationEventTarget")]
    internal static partial nint GetApplicationEventTarget();

    [LibraryImport(Carbon, EntryPoint = "InstallEventHandler")]
    internal static unsafe partial int InstallEventHandler(
        nint target,
        delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
        uint typeCount,
        NativeEventTypeSpec* eventTypes,
        nint userData,
        out nint handlerReference);

    [LibraryImport(Carbon, EntryPoint = "RemoveEventHandler")]
    internal static partial int RemoveEventHandler(nint handlerReference);

    [LibraryImport(Carbon, EntryPoint = "RegisterEventHotKey")]
    internal static partial int RegisterEventHotKey(
        uint virtualKey,
        uint modifiers,
        NativeEventHotKeyId hotKeyId,
        nint target,
        uint options,
        out nint hotKeyReference);

    [LibraryImport(Carbon, EntryPoint = "UnregisterEventHotKey")]
    internal static partial int UnregisterEventHotKey(nint hotKeyReference);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial double SendDouble(nint receiver, nint selector);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial NativeRectangle SendNativeRectangle(
        nint receiver,
        nint selector);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend_stret")]
    internal static partial void SendNativeRectangleStret(
        out NativeRectangle rectangle,
        nint receiver,
        nint selector);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidWithNUInt(
        nint receiver,
        nint selector,
        nuint argument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidWithInt32(
        nint receiver,
        nint selector,
        int argument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidWithNativePoint(
        nint receiver,
        nint selector,
        NativePoint point);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidWithNativeSize(
        nint receiver,
        nint selector,
        NativeSize size);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidWithNativeRectangleByte(
        nint receiver,
        nint selector,
        NativeRectangle rectangle,
        byte display);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithDouble(
        nint receiver,
        nint selector,
        double argument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithUInt32(
        nint receiver,
        nint selector,
        uint argument);
}
