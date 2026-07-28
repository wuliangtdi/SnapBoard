using System.Runtime.InteropServices;

namespace SnapBoard.Platform.MacOS.Interop;

internal static partial class MacOSNativeMethods
{
    private const string SystemConfiguration =
        "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";

    [LibraryImport(
        CoreFoundation,
        EntryPoint = "CFStringCreateWithCString",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint CFStringCreateWithCString(
        nint allocator,
        string value,
        uint encoding);

    [LibraryImport(CoreFoundation, EntryPoint = "CFArrayCreate")]
    internal static partial nint CFArrayCreate(
        nint allocator,
        nint values,
        nint count,
        nint callbacks);

    [LibraryImport(SystemConfiguration, EntryPoint = "SCDynamicStoreCreate")]
    internal static partial nint SCDynamicStoreCreate(
        nint allocator,
        nint name,
        nint callback,
        nint context);

    [LibraryImport(
        SystemConfiguration,
        EntryPoint = "SCDynamicStoreSetNotificationKeys")]
    internal static partial byte SCDynamicStoreSetNotificationKeys(
        nint store,
        nint keys,
        nint patterns);

    [LibraryImport(
        SystemConfiguration,
        EntryPoint = "SCDynamicStoreSetDispatchQueue")]
    internal static partial byte SCDynamicStoreSetDispatchQueue(
        nint store,
        nint queue);

    [LibraryImport(LibSystem, EntryPoint = "dispatch_get_global_queue")]
    internal static partial nint DispatchGetGlobalQueue(
        nint identifier,
        nuint flags);

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoidWithFourIntPtr(
        nint receiver,
        nint selector,
        nint firstArgument,
        nint secondArgument,
        nint thirdArgument,
        nint fourthArgument);
}
