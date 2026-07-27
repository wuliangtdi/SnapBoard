using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Clipboard;

internal sealed class WindowsClipboardReader(
    WindowsClipboardSettings settings,
    ClipboardOriginMarker originMarker)
{
    private const int ErrorAccessDenied = 5;
    private const int MaximumRegisteredFormatNameLength = 256;
    private const uint AllDroppedFiles = 0xFFFFFFFF;

    public ValueTask<ClipboardReadResult> ReadAsync(
        ClipboardChangedEvent change,
        CancellationToken cancellationToken) =>
        new(Task.Run(() => ReadCore(change, cancellationToken), cancellationToken));

    private ClipboardReadResult ReadCore(
        ClipboardChangedEvent change,
        CancellationToken cancellationToken)
    {
        bool openedAtLeastOnce = false;
        int lastError = 0;
        ClipboardSourceInfo source = ReadSource(change);

        for (int attempt = 0; attempt <= settings.OpenRetryDelays.Count; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!WindowsNativeMethods.OpenClipboard(0))
            {
                lastError = Marshal.GetLastPInvokeError();
                if (attempt < settings.OpenRetryDelays.Count)
                {
                    ClipboardRetryPolicy.Wait(settings.OpenRetryDelays[attempt], cancellationToken);
                    continue;
                }

                return new ClipboardReadResult(
                    ClipboardReadStatus.ClipboardBusy,
                    null,
                    ClipboardReadFailureReason.ClipboardBusy,
                    lastError);
            }

            openedAtLeastOnce = true;
            OpenedClipboardRead read;
            try
            {
                read = ReadOpenedClipboard(change, source);
            }
            finally
            {
                WindowsNativeMethods.CloseClipboard();
            }

            if (!read.HasTransientUnavailable || attempt >= settings.OpenRetryDelays.Count)
            {
                ClipboardReadStatus status = read.UnavailableFormats.Count == 0
                    ? ClipboardReadStatus.Success
                    : ClipboardReadStatus.Partial;
                ClipboardReadFailureReason failureReason = read.ContentTooLarge
                    ? ClipboardReadFailureReason.ContentTooLarge
                    : read.HasTransientUnavailable
                        ? ClipboardReadFailureReason.DelayedRenderingUnavailable
                        : ClipboardReadFailureReason.None;

                return new ClipboardReadResult(status, read.Snapshot, failureReason, read.NativeErrorCode);
            }

            // 延迟渲染的 owner 可能在第一次 GetClipboardData 时尚未返回数据。
            // 每次重试都先关闭剪贴板，再做短等待和重新打开，避免长时间独占全局剪贴板锁。
            ClipboardRetryPolicy.Wait(settings.OpenRetryDelays[attempt], cancellationToken);
        }

        return new ClipboardReadResult(
            ClipboardReadStatus.Failed,
            null,
            openedAtLeastOnce
                ? ClipboardReadFailureReason.NativeFailure
                : ClipboardReadFailureReason.ClipboardBusy,
            lastError);
    }

    private OpenedClipboardRead ReadOpenedClipboard(
        ClipboardChangedEvent change,
        ClipboardSourceInfo source)
    {
        List<ClipboardFormatDescriptor> formats = EnumerateFormats();
        HashSet<string> unavailableFormats = new(StringComparer.Ordinal);
        bool transientUnavailable = false;
        bool contentTooLarge = false;
        int nativeError = 0;
        int remainingPayloadBytes = settings.MaximumPayloadBytes;

        string? text = ReadText(
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);

        uint htmlFormat = WindowsNativeMethods.RegisterClipboardFormat("HTML Format");
        byte[]? html = ReadRegisteredBytes(
            htmlFormat,
            "HTML Format",
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);

        uint richTextFormat = WindowsNativeMethods.RegisterClipboardFormat("Rich Text Format");
        byte[]? richText = ReadRegisteredBytes(
            richTextFormat,
            "Rich Text Format",
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);

        ClipboardBitmapData? bitmap = ReadBitmap(
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);

        IReadOnlyList<string> files = ReadFilePaths(
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);

        bool isFromCurrentApplication = ReadOriginMarker(
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);

        uint currentSequence = WindowsNativeMethods.GetClipboardSequenceNumber();
        ClipboardContentSnapshot snapshot = new()
        {
            SequenceNumber = currentSequence == 0 ? change.SequenceNumber : currentSequence,
            CapturedAt = DateTimeOffset.UtcNow,
            Source = source,
            Formats = formats,
            UnavailableFormats = unavailableFormats.ToArray(),
            Text = text,
            Html = html ?? ReadOnlyMemory<byte>.Empty,
            RichText = richText ?? ReadOnlyMemory<byte>.Empty,
            Bitmap = bitmap,
            FilePaths = files,
            IsFromCurrentApplication = isFromCurrentApplication,
        };

        return new OpenedClipboardRead(
            snapshot,
            unavailableFormats.ToArray(),
            transientUnavailable,
            contentTooLarge,
            nativeError);
    }

    private static List<ClipboardFormatDescriptor> EnumerateFormats()
    {
        List<ClipboardFormatDescriptor> formats = [];
        uint format = 0;

        while (true)
        {
            Marshal.SetLastPInvokeError(0);
            format = WindowsNativeMethods.EnumClipboardFormats(format);
            if (format == 0)
            {
                break;
            }

            formats.Add(new ClipboardFormatDescriptor(
                $"windows:{format}",
                GetFormatName(format)));
        }

        return formats;
    }

    private static string? ReadText(
        HashSet<string> unavailableFormats,
        ref bool transientUnavailable,
        ref bool contentTooLarge,
        ref int nativeError,
        ref int remainingPayloadBytes)
    {
        string? unicode = ReadUnicodeText(
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);
        if (unicode is not null)
        {
            return unicode;
        }

        return ReadAnsiText(
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);
    }

    private static string? ReadUnicodeText(
        HashSet<string> unavailableFormats,
        ref bool transientUnavailable,
        ref bool contentTooLarge,
        ref int nativeError,
        ref int remainingPayloadBytes)
    {
        const string formatName = "CF_UNICODETEXT";
        if (!WindowsNativeMethods.IsClipboardFormatAvailable(
                WindowsNativeConstants.ClipboardFormatUnicodeText))
        {
            return null;
        }

        byte[]? bytes = ReadGlobalBytes(
            WindowsNativeConstants.ClipboardFormatUnicodeText,
            formatName,
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);
        if (bytes is null)
        {
            return null;
        }

        int byteLength = bytes.Length - (bytes.Length % sizeof(char));
        int terminator = byteLength;
        for (int index = 0; index + 1 < byteLength; index += sizeof(char))
        {
            if (bytes[index] == 0 && bytes[index + 1] == 0)
            {
                terminator = index;
                break;
            }
        }

        return Encoding.Unicode.GetString(bytes, 0, terminator);
    }

    private static string? ReadAnsiText(
        HashSet<string> unavailableFormats,
        ref bool transientUnavailable,
        ref bool contentTooLarge,
        ref int nativeError,
        ref int remainingPayloadBytes)
    {
        const string formatName = "CF_TEXT";
        if (!WindowsNativeMethods.IsClipboardFormatAvailable(
                WindowsNativeConstants.ClipboardFormatText))
        {
            return null;
        }

        byte[]? bytes = ReadGlobalBytes(
            WindowsNativeConstants.ClipboardFormatText,
            formatName,
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);
        if (bytes is null)
        {
            return null;
        }

        int terminator = Array.IndexOf(bytes, (byte)0);
        int byteLength = terminator < 0 ? bytes.Length : terminator;
        if (byteLength == 0)
        {
            return string.Empty;
        }

        unsafe
        {
            fixed (byte* source = bytes)
            {
                int characterCount = WindowsNativeMethods.MultiByteToWideChar(
                    WindowsNativeConstants.CodePageAnsi,
                    0,
                    source,
                    byteLength,
                    null,
                    0);
                if (characterCount == 0)
                {
                    unavailableFormats.Add(formatName);
                    nativeError = Marshal.GetLastPInvokeError();
                    return null;
                }

                char[] characters = new char[characterCount];
                fixed (char* destination = characters)
                {
                    int converted = WindowsNativeMethods.MultiByteToWideChar(
                        WindowsNativeConstants.CodePageAnsi,
                        0,
                        source,
                        byteLength,
                        destination,
                        characters.Length);
                    if (converted == 0)
                    {
                        unavailableFormats.Add(formatName);
                        nativeError = Marshal.GetLastPInvokeError();
                        return null;
                    }

                    return new string(characters, 0, converted);
                }
            }
        }
    }

    private static byte[]? ReadRegisteredBytes(
        uint format,
        string formatName,
        HashSet<string> unavailableFormats,
        ref bool transientUnavailable,
        ref bool contentTooLarge,
        ref int nativeError,
        ref int remainingPayloadBytes)
    {
        if (format == 0 || !WindowsNativeMethods.IsClipboardFormatAvailable(format))
        {
            return null;
        }

        byte[]? bytes = ReadGlobalBytes(
            format,
            formatName,
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);
        if (bytes is null)
        {
            return null;
        }

        return TrimTrailingNull(bytes);
    }

    private static ClipboardBitmapData? ReadBitmap(
        HashSet<string> unavailableFormats,
        ref bool transientUnavailable,
        ref bool contentTooLarge,
        ref int nativeError,
        ref int remainingPayloadBytes)
    {
        uint format;
        ClipboardBitmapEncoding encoding;
        string formatName;

        if (WindowsNativeMethods.IsClipboardFormatAvailable(
                WindowsNativeConstants.ClipboardFormatDeviceIndependentBitmapV5))
        {
            format = WindowsNativeConstants.ClipboardFormatDeviceIndependentBitmapV5;
            encoding = ClipboardBitmapEncoding.DeviceIndependentBitmapV5;
            formatName = "CF_DIBV5";
        }
        else if (WindowsNativeMethods.IsClipboardFormatAvailable(
                     WindowsNativeConstants.ClipboardFormatDeviceIndependentBitmap))
        {
            format = WindowsNativeConstants.ClipboardFormatDeviceIndependentBitmap;
            encoding = ClipboardBitmapEncoding.DeviceIndependentBitmap;
            formatName = "CF_DIB";
        }
        else
        {
            uint pngFormat = WindowsNativeMethods.RegisterClipboardFormat("PNG");
            if (pngFormat == 0 || !WindowsNativeMethods.IsClipboardFormatAvailable(pngFormat))
            {
                return null;
            }

            format = pngFormat;
            encoding = ClipboardBitmapEncoding.PortableNetworkGraphics;
            formatName = "PNG";
        }

        byte[]? bytes = ReadGlobalBytes(
            format,
            formatName,
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);
        if (bytes is null)
        {
            return null;
        }

        (int width, int height, ushort bitsPerPixel) =
            encoding == ClipboardBitmapEncoding.PortableNetworkGraphics
                ? ReadPngMetadata(bytes)
                : ReadBitmapMetadata(bytes);
        return new ClipboardBitmapData(encoding, bytes, width, height, bitsPerPixel);
    }

    private IReadOnlyList<string> ReadFilePaths(
        HashSet<string> unavailableFormats,
        ref bool transientUnavailable,
        ref bool contentTooLarge,
        ref int nativeError,
        ref int remainingPayloadBytes)
    {
        const string formatName = "CF_HDROP";
        if (!WindowsNativeMethods.IsClipboardFormatAvailable(
                WindowsNativeConstants.ClipboardFormatFileDrop))
        {
            return Array.Empty<string>();
        }

        nint dropHandle = WindowsNativeMethods.GetClipboardData(
            WindowsNativeConstants.ClipboardFormatFileDrop);
        if (dropHandle == 0)
        {
            unavailableFormats.Add(formatName);
            transientUnavailable = true;
            nativeError = Marshal.GetLastPInvokeError();
            return Array.Empty<string>();
        }

        uint count;
        unsafe
        {
            count = WindowsNativeMethods.DragQueryFile(dropHandle, AllDroppedFiles, null, 0);
        }

        if (count > settings.MaximumFileCount)
        {
            unavailableFormats.Add(formatName);
            contentTooLarge = true;
            return Array.Empty<string>();
        }

        List<string> paths = new((int)count);
        long totalCharacterCount = 1;

        for (uint index = 0; index < count; index++)
        {
            uint length;
            unsafe
            {
                length = WindowsNativeMethods.DragQueryFile(dropHandle, index, null, 0);
            }

            totalCharacterCount += length + 1;
            if (totalCharacterCount * sizeof(char) > remainingPayloadBytes)
            {
                unavailableFormats.Add(formatName);
                contentTooLarge = true;
                return Array.Empty<string>();
            }

            char[] buffer = new char[length + 1];
            unsafe
            {
                fixed (char* bufferPointer = buffer)
                {
                    uint copied = WindowsNativeMethods.DragQueryFile(
                        dropHandle,
                        index,
                        bufferPointer,
                        (uint)buffer.Length);
                    if (copied == 0 && length != 0)
                    {
                        unavailableFormats.Add(formatName);
                        transientUnavailable = true;
                        nativeError = Marshal.GetLastPInvokeError();
                        return Array.Empty<string>();
                    }
                }
            }

            paths.Add(new string(buffer, 0, (int)length));
        }

        remainingPayloadBytes -= checked((int)(totalCharacterCount * sizeof(char)));
        return paths;
    }

    private bool ReadOriginMarker(
        HashSet<string> unavailableFormats,
        ref bool transientUnavailable,
        ref bool contentTooLarge,
        ref int nativeError,
        ref int remainingPayloadBytes)
    {
        uint markerFormat = originMarker.GetFormatId();
        if (markerFormat == 0 || !WindowsNativeMethods.IsClipboardFormatAvailable(markerFormat))
        {
            return false;
        }

        byte[]? marker = ReadGlobalBytes(
            markerFormat,
            ClipboardOriginMarker.FormatName,
            unavailableFormats,
            ref transientUnavailable,
            ref contentTooLarge,
            ref nativeError,
            ref remainingPayloadBytes);
        return marker is not null && originMarker.Matches(marker);
    }

    private static byte[]? ReadGlobalBytes(
        uint format,
        string formatName,
        HashSet<string> unavailableFormats,
        ref bool transientUnavailable,
        ref bool contentTooLarge,
        ref int nativeError,
        ref int remainingPayloadBytes)
    {
        nint memoryHandle = WindowsNativeMethods.GetClipboardData(format);
        if (memoryHandle == 0)
        {
            unavailableFormats.Add(formatName);
            transientUnavailable = true;
            nativeError = Marshal.GetLastPInvokeError();
            return null;
        }

        nuint nativeSize = WindowsNativeMethods.GlobalSize(memoryHandle);
        if (nativeSize == 0)
        {
            unavailableFormats.Add(formatName);
            transientUnavailable = true;
            nativeError = Marshal.GetLastPInvokeError();
            return null;
        }

        if (nativeSize > (nuint)remainingPayloadBytes || nativeSize > int.MaxValue)
        {
            unavailableFormats.Add(formatName);
            contentTooLarge = true;
            return null;
        }

        nint memory = WindowsNativeMethods.GlobalLock(memoryHandle);
        if (memory == 0)
        {
            unavailableFormats.Add(formatName);
            transientUnavailable = true;
            nativeError = Marshal.GetLastPInvokeError();
            return null;
        }

        try
        {
            byte[] bytes = new byte[(int)nativeSize];
            remainingPayloadBytes -= bytes.Length;
            Marshal.Copy(memory, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            WindowsNativeMethods.GlobalUnlock(memoryHandle);
        }
    }

    private static ClipboardSourceInfo ReadSource(ClipboardChangedEvent change)
    {
        uint currentSequence = WindowsNativeMethods.GetClipboardSequenceNumber();
        bool hintMatchesCurrentClipboard = currentSequence == 0 ||
            (change.SequenceNumber <= uint.MaxValue && currentSequence == (uint)change.SequenceNumber);
        if (hintMatchesCurrentClipboard)
        {
            ClipboardSourceInfo? owner = ReadHintedProcess(
                change.SourceHint.ClipboardOwnerProcessId,
                ClipboardSourceAttributionKind.ClipboardOwnerAtChange);
            if (owner?.AccessStatus == ClipboardSourceAccessStatus.Identified)
            {
                return owner;
            }

            ClipboardSourceInfo? foreground =
                change.SourceHint.ForegroundProcessId == change.SourceHint.ClipboardOwnerProcessId
                    ? null
                    : ReadHintedProcess(
                        change.SourceHint.ForegroundProcessId,
                        ClipboardSourceAttributionKind.ForegroundWindowAtChange);
            if (foreground?.AccessStatus == ClipboardSourceAccessStatus.Identified)
            {
                return foreground;
            }

            if (owner is not null || foreground is not null)
            {
                return owner ?? foreground!;
            }
        }

        nint ownerWindow = WindowsNativeMethods.GetClipboardOwner();
        if (ownerWindow == 0 ||
            WindowsNativeMethods.GetWindowThreadProcessId(ownerWindow, out uint processId) == 0 ||
            processId is 0 or > int.MaxValue)
        {
            return CreateUnknownSource();
        }

        return ReadProcessSource(
            (int)processId,
            ClipboardSourceAttributionKind.ClipboardOwnerAtRead);
    }

    private static ClipboardSourceInfo? ReadHintedProcess(
        int? processId,
        ClipboardSourceAttributionKind attributionKind) =>
        processId is > 0
            ? ReadProcessSource(processId.Value, attributionKind)
            : null;

    internal static ClipboardSourceInfo ReadProcessSource(
        int processId,
        ClipboardSourceAttributionKind attributionKind)
    {
        nint process = WindowsNativeMethods.OpenProcess(
            WindowsNativeConstants.ProcessQueryLimitedInformation,
            false,
            (uint)processId);
        if (process == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            return new ClipboardSourceInfo(
                processId,
                null,
                null,
                error == ErrorAccessDenied
                    ? ClipboardSourceAccessStatus.AccessDenied
                    : ClipboardSourceAccessStatus.PathUnavailable,
                AttributionKind: attributionKind);
        }

        try
        {
            string? executablePath = ReadExecutablePath(process, out int pathError);
            string? applicationUserModelId = ReadAppModelValue(process, applicationId: true);
            string? packageFamilyName = ReadAppModelValue(process, applicationId: false);
            bool identified = executablePath is not null ||
                applicationUserModelId is not null ||
                packageFamilyName is not null;
            return new ClipboardSourceInfo(
                processId,
                executablePath is null ? null : Path.GetFileNameWithoutExtension(executablePath),
                executablePath,
                identified
                    ? ClipboardSourceAccessStatus.Identified
                    : pathError == ErrorAccessDenied
                        ? ClipboardSourceAccessStatus.AccessDenied
                        : ClipboardSourceAccessStatus.PathUnavailable,
                applicationUserModelId,
                packageFamilyName,
                attributionKind);
        }
        finally
        {
            WindowsNativeMethods.CloseHandle(process);
        }
    }

    private static unsafe string? ReadExecutablePath(nint process, out int nativeError)
    {
        char[] pathBuffer = new char[32768];
        uint pathLength = (uint)pathBuffer.Length;
        fixed (char* pathPointer = pathBuffer)
        {
            if (!WindowsNativeMethods.QueryFullProcessImageName(
                    process,
                    0,
                    pathPointer,
                    &pathLength))
            {
                nativeError = Marshal.GetLastPInvokeError();
                return null;
            }
        }

        nativeError = 0;
        return new string(pathBuffer, 0, (int)pathLength);
    }

    private static unsafe string? ReadAppModelValue(nint process, bool applicationId)
    {
        uint length = 0;
        int result = applicationId
            ? WindowsNativeMethods.GetApplicationUserModelId(process, &length, null)
            : WindowsNativeMethods.GetPackageFamilyName(process, &length, null);
        if (result != WindowsNativeConstants.ErrorInsufficientBuffer || length is <= 1 or > 1024)
        {
            return null;
        }

        char[] buffer = new char[checked((int)length)];
        fixed (char* bufferPointer = buffer)
        {
            result = applicationId
                ? WindowsNativeMethods.GetApplicationUserModelId(process, &length, bufferPointer)
                : WindowsNativeMethods.GetPackageFamilyName(process, &length, bufferPointer);
        }

        if (result != 0 || length <= 1)
        {
            return null;
        }

        return new string(buffer, 0, (int)length - 1);
    }

    private static ClipboardSourceInfo CreateUnknownSource() => new(
        null,
        null,
        null,
        ClipboardSourceAccessStatus.Unknown);

    private static string GetFormatName(uint format) => format switch
    {
        WindowsNativeConstants.ClipboardFormatText => "CF_TEXT",
        WindowsNativeConstants.ClipboardFormatBitmap => "CF_BITMAP",
        WindowsNativeConstants.ClipboardFormatDeviceIndependentBitmap => "CF_DIB",
        WindowsNativeConstants.ClipboardFormatUnicodeText => "CF_UNICODETEXT",
        WindowsNativeConstants.ClipboardFormatFileDrop => "CF_HDROP",
        WindowsNativeConstants.ClipboardFormatLocale => "CF_LOCALE",
        WindowsNativeConstants.ClipboardFormatDeviceIndependentBitmapV5 => "CF_DIBV5",
        _ => GetRegisteredFormatName(format),
    };

    private static string GetRegisteredFormatName(uint format)
    {
        char[] buffer = new char[MaximumRegisteredFormatNameLength];
        int length;
        unsafe
        {
            fixed (char* bufferPointer = buffer)
            {
                length = WindowsNativeMethods.GetClipboardFormatName(
                    format,
                    bufferPointer,
                    buffer.Length);
            }
        }

        return length > 0 ? new string(buffer, 0, length) : $"Format {format}";
    }

    private static byte[] TrimTrailingNull(byte[] bytes)
    {
        int length = bytes.Length;
        while (length > 0 && bytes[length - 1] == 0)
        {
            length--;
        }

        return length == bytes.Length ? bytes : bytes[..length];
    }

    private static (int Width, int Height, ushort BitsPerPixel) ReadBitmapMetadata(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < sizeof(uint))
        {
            return (0, 0, 0);
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (headerSize >= 40 && bytes.Length >= 16)
        {
            int width = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
            int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            int height = signedHeight == int.MinValue ? int.MaxValue : Math.Abs(signedHeight);
            ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
            return (width, height, bitsPerPixel);
        }

        if (headerSize == 12 && bytes.Length >= 12)
        {
            int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
            ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
            return (width, height, bitsPerPixel);
        }

        return (0, 0, 0);
    }

    private static (int Width, int Height, ushort BitsPerPixel) ReadPngMetadata(
        ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 29 ||
            !bytes[..signature.Length].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return (0, 0, 0);
        }

        uint rawWidth = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        uint rawHeight = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        if (rawWidth is 0 or > int.MaxValue || rawHeight is 0 or > int.MaxValue)
        {
            return (0, 0, 0);
        }

        int channels = bytes[25] switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        int bitsPerPixel = bytes[24] * channels;
        if (channels == 0 || bitsPerPixel > ushort.MaxValue)
        {
            return (0, 0, 0);
        }

        return ((int)rawWidth, (int)rawHeight, (ushort)bitsPerPixel);
    }

    private sealed record OpenedClipboardRead(
        ClipboardContentSnapshot Snapshot,
        IReadOnlyList<string> UnavailableFormats,
        bool HasTransientUnavailable,
        bool ContentTooLarge,
        int NativeErrorCode);
}
