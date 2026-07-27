using System.Runtime.InteropServices;

namespace SnapBoard.Platform.Windows.Interop;

internal static partial class WindowsNativeConstants
{
    public const uint ClipboardFormatText = 1;
    public const uint ClipboardFormatBitmap = 2;
    public const uint ClipboardFormatDeviceIndependentBitmap = 8;
    public const uint ClipboardFormatUnicodeText = 13;
    public const uint ClipboardFormatFileDrop = 15;
    public const uint ClipboardFormatLocale = 16;
    public const uint ClipboardFormatDeviceIndependentBitmapV5 = 17;

    public const uint WindowMessageClose = 0x0010;
    public const uint WindowMessageDestroy = 0x0002;
    public const uint WindowMessageNonClientCreate = 0x0081;
    public const uint WindowMessageNonClientDestroy = 0x0082;
    public const uint WindowMessageClipboardUpdate = 0x031D;
    public const uint WindowMessageHotKey = 0x0312;
    public const uint WindowMessageRenderFormat = 0x0305;
    public const uint WindowMessageRenderAllFormats = 0x0306;
    public const uint WindowMessageQuit = 0x0012;
    public const uint WindowMessageHotKeyCommand = 0x8001;

    public const int WindowLongUserData = -21;
    public static readonly nint MessageOnlyWindowParent = new(-3);

    public const uint GlobalMemoryMoveable = 0x0002;
    public const uint GlobalMemoryZeroInitialize = 0x0040;

    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const int ErrorInsufficientBuffer = 122;
    public const uint TokenQuery = 0x0008;
    public const int TokenIntegrityLevel = 25;

    public const uint InputKeyboard = 1;
    public const uint CodePageAnsi = 0;
    public const uint KeyEventKeyUp = 0x0002;
    public const ushort VirtualKeyControl = 0x11;
    public const ushort VirtualKeyV = 0x56;

    public const int ErrorHotKeyAlreadyRegistered = 1409;
    public const uint MonitorDefaultToNearest = 0x00000002;
    public const uint SetWindowPositionNoActivate = 0x0010;
    public const uint ShowWindowMaximized = 3;
    public const uint ShowWindowRestore = 9;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WindowClassEx
{
    public uint Size;
    public uint Style;
    public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> WindowProcedure;
    public int ClassExtraBytes;
    public int WindowExtraBytes;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint BackgroundBrush;
    public char* MenuName;
    public char* ClassName;
    public nint SmallIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRectangle
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMonitorInfo
{
    public uint Size;
    public NativeRectangle Monitor;
    public NativeRectangle WorkArea;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    public nint WindowHandle;
    public uint Message;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public NativePoint Point;
    public uint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CreateStruct
{
    public nint CreateParameters;
    public nint Instance;
    public nint Menu;
    public nint Parent;
    public int Height;
    public int Width;
    public int Y;
    public int X;
    public int Style;
    public nint Name;
    public nint Class;
    public uint ExtendedStyle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)]
    public KeyboardInput Keyboard;

    // INPUT 的 union 大小由 MOUSEINPUT 决定。即使这里只发送键盘事件，也必须保留
    // 该字段让 x64 cbSize 为 40（x86 为 28），否则 SendInput 会直接拒绝整个批次。
    [FieldOffset(0)]
    public MouseInput Mouse;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInput
{
    public uint Type;
    public InputUnion Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SidAndAttributes
{
    public nint Sid;
    public uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TokenMandatoryLabel
{
    public SidAndAttributes Label;
}
