using System.Runtime.InteropServices;

namespace SnapBoard.Platform.Windows.Interop;

internal static partial class WindowsNativeMethods
{
    [LibraryImport(
        Advapi32,
        EntryPoint = "CredReadW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredentialRead(
        string targetName,
        uint type,
        uint flags,
        out nint credential);

    [LibraryImport(Advapi32, EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CredentialWrite(
        NativeCredential* credential,
        uint flags);

    [LibraryImport(
        Advapi32,
        EntryPoint = "CredDeleteW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredentialDelete(
        string targetName,
        uint type,
        uint flags);

    [LibraryImport(Advapi32, EntryPoint = "CredFree")]
    internal static partial void CredentialFree(nint buffer);
}
