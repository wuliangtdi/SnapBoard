using System.Runtime.InteropServices;

namespace SnapBoard.Platform.Windows.Interop;

internal static partial class WindowsStorageNativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    [LibraryImport(
        Kernel32,
        EntryPoint = "GetVolumePathNameW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetVolumePathName(
        string fileName,
        char* volumePathName,
        uint bufferLength);

    [LibraryImport(
        Kernel32,
        EntryPoint = "GetVolumeNameForVolumeMountPointW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        char* volumeName,
        uint bufferLength);
}
