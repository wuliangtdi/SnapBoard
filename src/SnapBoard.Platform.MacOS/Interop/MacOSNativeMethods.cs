using System.Runtime.InteropServices;

namespace SnapBoard.Platform.MacOS.Interop;

internal static partial class MacOSNativeMethods
{
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ObjectiveCRuntime = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(AppKit, EntryPoint = "NSApplicationLoad")]
    internal static partial byte NSApplicationLoad();

    [LibraryImport(
        ObjectiveCRuntime,
        EntryPoint = "objc_getClass",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetClass(string name);

    [LibraryImport(
        ObjectiveCRuntime,
        EntryPoint = "sel_registerName",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint RegisterSelector(string name);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_autoreleasePoolPush")]
    internal static partial nint PushAutoreleasePool();

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_autoreleasePoolPop")]
    internal static partial void PopAutoreleasePool(nint token);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtr(nint receiver, nint selector);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithIntPtr(
        nint receiver,
        nint selector,
        nint argument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithNUInt(
        nint receiver,
        nint selector,
        nuint argument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithInt32(
        nint receiver,
        nint selector,
        int argument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithIntPtrByte(
        nint receiver,
        nint selector,
        nint firstArgument,
        byte secondArgument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithIntPtrNUInt(
        nint receiver,
        nint selector,
        nint firstArgument,
        nuint secondArgument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithIntPtrNUIntNUInt(
        nint receiver,
        nint selector,
        nint firstArgument,
        nuint secondArgument,
        nuint thirdArgument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nuint SendNUInt(nint receiver, nint selector);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial int SendInt32(nint receiver, nint selector);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBool(nint receiver, nint selector);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBoolWithIntPtr(
        nint receiver,
        nint selector,
        nint argument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBoolWithNUInt(
        nint receiver,
        nint selector,
        nuint argument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial byte SendBoolWithIntPtrIntPtr(
        nint receiver,
        nint selector,
        nint firstArgument,
        nint secondArgument);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidWithIntPtr(
        nint receiver,
        nint selector,
        nint argument);

    [LibraryImport(ApplicationServices, EntryPoint = "AXIsProcessTrusted")]
    internal static partial byte AXIsProcessTrusted();

    [LibraryImport(CoreGraphics, EntryPoint = "CGPreflightPostEventAccess")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool CGPreflightPostEventAccess();

    [LibraryImport(CoreGraphics, EntryPoint = "CGEventCreateKeyboardEvent")]
    internal static partial nint CGEventCreateKeyboardEvent(
        nint source,
        ushort virtualKey,
        [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [LibraryImport(CoreGraphics, EntryPoint = "CGEventSetFlags")]
    internal static partial void CGEventSetFlags(nint keyboardEvent, ulong flags);

    [LibraryImport(CoreGraphics, EntryPoint = "CGEventPost")]
    internal static partial void CGEventPost(int tapLocation, nint keyboardEvent);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    internal static partial void CFRelease(nint handle);
}
