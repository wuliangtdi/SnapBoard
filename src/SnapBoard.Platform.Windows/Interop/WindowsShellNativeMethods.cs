using System.Runtime.InteropServices;

namespace SnapBoard.Platform.Windows.Interop;

internal static partial class WindowsNativeMethods
{
    private const string Gdi32 = "gdi32.dll";

    [LibraryImport(
        Shell32,
        EntryPoint = "SHGetFileInfoW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    internal static unsafe partial nuint GetShellFileInfo(
        string path,
        uint fileAttributes,
        ShellFileInfo* fileInfo,
        uint fileInfoSize,
        uint flags);

    [LibraryImport(User32, EntryPoint = "GetDC", SetLastError = true)]
    internal static partial nint GetDeviceContext(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "ReleaseDC")]
    internal static partial int ReleaseDeviceContext(nint windowHandle, nint deviceContext);

    [LibraryImport(User32, EntryPoint = "DrawIconEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawIcon(
        nint deviceContext,
        int x,
        int y,
        nint icon,
        int width,
        int height,
        uint animationStep,
        nint flickerFreeBrush,
        uint flags);

    [LibraryImport(User32, EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    [LibraryImport(User32, EntryPoint = "GetGuiResources", SetLastError = true)]
    internal static partial uint GetGuiResources(nint process, uint flags);

    [LibraryImport(Gdi32, EntryPoint = "CreateCompatibleDC", SetLastError = true)]
    internal static partial nint CreateCompatibleDeviceContext(nint deviceContext);

    [LibraryImport(Gdi32, EntryPoint = "DeleteDC", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDeviceContext(nint deviceContext);

    [LibraryImport(Gdi32, EntryPoint = "CreateDIBSection", SetLastError = true)]
    internal static unsafe partial nint CreateDeviceIndependentBitmapSection(
        nint deviceContext,
        NativeBitmapInfo* bitmapInfo,
        uint usage,
        out nint pixelBits,
        nint section,
        uint offset);

    [LibraryImport(Gdi32, EntryPoint = "SelectObject", SetLastError = true)]
    internal static partial nint SelectGraphicsObject(nint deviceContext, nint graphicsObject);

    [LibraryImport(Gdi32, EntryPoint = "DeleteObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteGraphicsObject(nint graphicsObject);
}
