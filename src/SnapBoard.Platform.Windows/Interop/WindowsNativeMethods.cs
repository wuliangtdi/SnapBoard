using System.Runtime.InteropServices;

namespace SnapBoard.Platform.Windows.Interop;

internal static partial class WindowsNativeMethods
{
    private const string Advapi32 = "advapi32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shell32 = "shell32.dll";
    private const string User32 = "user32.dll";

    [LibraryImport(User32, EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static unsafe partial ushort RegisterClassEx(WindowClassEx* windowClass);

    [LibraryImport(
        User32,
        EntryPoint = "UnregisterClassW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterClass(string className, nint instance);

    [LibraryImport(
        User32,
        EntryPoint = "CreateWindowExW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    internal static partial nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport(User32, EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport(User32, EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "GetMessageW", SetLastError = true)]
    internal static partial int GetMessage(
        out NativeMessage message,
        nint windowHandle,
        uint minimumMessage,
        uint maximumMessage);

    [LibraryImport(User32, EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in NativeMessage message);

    [LibraryImport(User32, EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in NativeMessage message);

    [LibraryImport(User32, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport(User32, EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport(User32, EntryPoint = "PostQuitMessage")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPointer(
        nint windowHandle,
        int index,
        nint value);

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPointer(nint windowHandle, int index);

    [LibraryImport(User32, EntryPoint = "AddClipboardFormatListener", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AddClipboardFormatListener(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "RemoveClipboardFormatListener", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveClipboardFormatListener(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "GetClipboardSequenceNumber")]
    internal static partial uint GetClipboardSequenceNumber();

    [LibraryImport(User32, EntryPoint = "OpenClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint ownerWindow);

    [LibraryImport(User32, EntryPoint = "CloseClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport(User32, EntryPoint = "EmptyClipboard", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport(User32, EntryPoint = "EnumClipboardFormats", SetLastError = true)]
    internal static partial uint EnumClipboardFormats(uint format);

    [LibraryImport(User32, EntryPoint = "GetClipboardData", SetLastError = true)]
    internal static partial nint GetClipboardData(uint format);

    [LibraryImport(User32, EntryPoint = "SetClipboardData", SetLastError = true)]
    internal static partial nint SetClipboardData(uint format, nint memoryHandle);

    [LibraryImport(
        User32,
        EntryPoint = "RegisterClipboardFormatW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    internal static partial uint RegisterClipboardFormat(string formatName);

    [LibraryImport(User32, EntryPoint = "GetClipboardFormatNameW", SetLastError = true)]
    internal static unsafe partial int GetClipboardFormatName(
        uint format,
        char* formatName,
        int maximumCount);

    [LibraryImport(User32, EntryPoint = "IsClipboardFormatAvailable")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport(User32, EntryPoint = "GetClipboardOwner")]
    internal static partial nint GetClipboardOwner();

    [LibraryImport(User32, EntryPoint = "GetWindowThreadProcessId")]
    internal static partial uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [LibraryImport(User32, EntryPoint = "GetForegroundWindow")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport(User32, EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(
        nint windowHandle,
        int identifier,
        uint modifiers,
        uint virtualKey);

    [LibraryImport(User32, EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint windowHandle, int identifier);

    [LibraryImport(User32, EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRectangle(
        nint windowHandle,
        out NativeRectangle rectangle);

    [LibraryImport(User32, EntryPoint = "IsZoomed")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint windowHandle, uint command);

    [LibraryImport(User32, EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPosition(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport(User32, EntryPoint = "MonitorFromWindow")]
    internal static partial nint MonitorFromWindow(nint windowHandle, uint flags);

    [LibraryImport(User32, EntryPoint = "MonitorFromRect")]
    internal static partial nint MonitorFromRectangle(
        in NativeRectangle rectangle,
        uint flags);

    [LibraryImport(User32, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(
        nint monitor,
        ref NativeMonitorInfo monitorInfo);

    [LibraryImport(User32, EntryPoint = "GetDpiForWindow")]
    internal static partial uint GetDpiForWindow(nint windowHandle);

    [LibraryImport(User32, EntryPoint = "GetAsyncKeyState")]
    internal static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport(User32, EntryPoint = "SendInput", SetLastError = true)]
    internal static unsafe partial uint SendInput(
        uint inputCount,
        NativeInput* inputs,
        int inputSize);

    [LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport(Kernel32, EntryPoint = "GetCurrentThreadId")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport(Kernel32, EntryPoint = "GetCurrentProcess")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport(Kernel32, EntryPoint = "MultiByteToWideChar", SetLastError = true)]
    internal static unsafe partial int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte* source,
        int sourceLength,
        char* destination,
        int destinationLength);

    [LibraryImport(Kernel32, EntryPoint = "GlobalAlloc", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport(Kernel32, EntryPoint = "GlobalLock", SetLastError = true)]
    internal static partial nint GlobalLock(nint memoryHandle);

    [LibraryImport(Kernel32, EntryPoint = "GlobalUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint memoryHandle);

    [LibraryImport(Kernel32, EntryPoint = "GlobalSize", SetLastError = true)]
    internal static partial nuint GlobalSize(nint memoryHandle);

    [LibraryImport(Kernel32, EntryPoint = "GlobalFree", SetLastError = true)]
    internal static partial nint GlobalFree(nint memoryHandle);

    [LibraryImport(Kernel32, EntryPoint = "OpenProcess", SetLastError = true)]
    internal static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport(Kernel32, EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport(Kernel32, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(
        nint process,
        uint flags,
        char* executableName,
        uint* size);

    [LibraryImport(Advapi32, EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out nint token);

    [LibraryImport(Advapi32, EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetTokenInformation(
        nint token,
        int informationClass,
        void* tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport(Advapi32, EntryPoint = "GetSidSubAuthorityCount")]
    internal static partial nint GetSidSubAuthorityCount(nint sid);

    [LibraryImport(Advapi32, EntryPoint = "GetSidSubAuthority")]
    internal static partial nint GetSidSubAuthority(nint sid, uint subAuthority);

    [LibraryImport(Shell32, EntryPoint = "DragQueryFileW", SetLastError = true)]
    internal static unsafe partial uint DragQueryFile(
        nint dropHandle,
        uint fileIndex,
        char* fileName,
        uint characterCount);
}
