using System.Runtime.InteropServices;

namespace SnapBoard.Platform.Windows.Interop;

internal static partial class WindowsNativeConstants
{
    public const uint CredentialTypeGeneric = 1;
    public const uint CredentialPersistLocalMachine = 2;
    public const int MaximumCredentialBlobSize = 5 * 512;

    public const int ErrorAccessDenied = 5;
    public const int ErrorCancelled = 1223;
    public const int ErrorNoSuchLogonSession = 1312;
    public const int ErrorNotFound = 1168;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFileTime
{
    public uint LowDateTime;
    public uint HighDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCredential
{
    public uint Flags;
    public uint Type;
    public nint TargetName;
    public nint Comment;
    public NativeFileTime LastWritten;
    public uint CredentialBlobSize;
    public nint CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public nint Attributes;
    public nint TargetAlias;
    public nint UserName;
}
