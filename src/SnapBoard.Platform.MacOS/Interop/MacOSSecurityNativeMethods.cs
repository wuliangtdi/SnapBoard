using System.Runtime.InteropServices;

namespace SnapBoard.Platform.MacOS.Interop;

internal static partial class MacOSNativeMethods
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";

    [LibraryImport(Security, EntryPoint = "SecKeychainAddGenericPassword")]
    internal static unsafe partial int SecKeychainAddGenericPassword(
        nint keychain,
        uint serviceLength,
        byte* service,
        uint accountLength,
        byte* account,
        uint passwordLength,
        byte* password,
        out nint item);

    [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    internal static unsafe partial int SecKeychainFindGenericPassword(
        nint keychainOrArray,
        uint serviceLength,
        byte* service,
        uint accountLength,
        byte* account,
        out uint passwordLength,
        out nint passwordData,
        out nint item);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    internal static unsafe partial int SecKeychainItemModifyAttributesAndData(
        nint item,
        nint attributes,
        uint dataLength,
        byte* data);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemFreeContent")]
    internal static partial int SecKeychainItemFreeContent(nint attributes, nint data);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemDelete")]
    internal static partial int SecKeychainItemDelete(nint item);

    [LibraryImport(ApplicationServices, EntryPoint = "AXIsProcessTrustedWithOptions")]
    internal static partial byte AXIsProcessTrustedWithOptions(nint options);

    [LibraryImport(CoreGraphics, EntryPoint = "CGRequestPostEventAccess")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool CGRequestPostEventAccess();

    [LibraryImport(ObjectiveCRuntime, EntryPoint = "objc_msgSend")]
    internal static partial nint SendIntPtrWithByte(
        nint receiver,
        nint selector,
        byte argument);
}
