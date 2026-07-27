using System.Runtime.InteropServices;

namespace SnapBoard.Platform.Windows.Interop;

internal static partial class WindowsNativeConstants
{
    public const uint ShellFileInfoIcon = 0x00000100;
    public const uint ShellFileInfoDisplayName = 0x00000200;
    public const uint ShellFileInfoLargeIcon = 0x00000000;
    public const uint DrawIconNormal = 0x00000003;
    public const uint DibRgbColors = 0;
    public const uint BitmapCompressionRgb = 0;
    public const uint GuiResourcesGdiObjects = 0;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ShellFileInfo
{
    public nint IconHandle;
    public int IconIndex;
    public uint Attributes;
    public fixed char DisplayName[260];
    public fixed char TypeName[80];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeBitmapInfoHeader
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitsPerPixel;
    public uint Compression;
    public uint ImageSize;
    public int HorizontalPixelsPerMeter;
    public int VerticalPixelsPerMeter;
    public uint ColorsUsed;
    public uint ImportantColors;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRgbQuad
{
    public byte Blue;
    public byte Green;
    public byte Red;
    public byte Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeBitmapInfo
{
    public NativeBitmapInfoHeader Header;
    public NativeRgbQuad Colors;
}
