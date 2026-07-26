using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Clipboard;

internal sealed class WindowsClipboardWriter(
    WindowsClipboardSettings settings,
    ClipboardOriginMarker originMarker,
    ClipboardFeedbackGuard feedbackGuard)
{
    private const int DropFilesHeaderSize = 20;

    public ValueTask<ClipboardWriteResult> WriteAsync(
        ClipboardWriteRequest request,
        nint ownerWindow,
        CancellationToken cancellationToken) =>
        new(Task.Run(
            () => WriteCore(request, ownerWindow, cancellationToken),
            cancellationToken));

    private ClipboardWriteResult WriteCore(
        ClipboardWriteRequest request,
        nint ownerWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.HasContent || ownerWindow == 0)
        {
            return new ClipboardWriteResult(ClipboardWriteStatus.InvalidContent);
        }

        PayloadBuildResult build = BuildPayloads(request);
        if (build.InvalidContent)
        {
            return new ClipboardWriteResult(ClipboardWriteStatus.InvalidContent);
        }

        if (build.ErrorCode != 0 || build.Payloads.Count == 0)
        {
            return new ClipboardWriteResult(
                ClipboardWriteStatus.Failed,
                NativeErrorCode: build.ErrorCode);
        }

        List<OwnedGlobalMemory> allocations = [];
        bool clipboardOpen = false;
        ClipboardWriteStatus status = ClipboardWriteStatus.Failed;
        bool feedbackMarkerWritten = false;
        int nativeError = 0;
        uint sequenceBeforeClose = 0;

        try
        {
            foreach (ClipboardPayload payload in build.Payloads)
            {
                OwnedGlobalMemory? allocation = OwnedGlobalMemory.TryCreate(payload);
                if (allocation is null)
                {
                    nativeError = Marshal.GetLastPInvokeError();
                    return new ClipboardWriteResult(
                        ClipboardWriteStatus.Failed,
                        NativeErrorCode: nativeError);
                }

                allocations.Add(allocation);
            }

            // OpenClipboard 失败通常是其他进程短暂占用。重试全部发生在后台工作线程，
            // 总等待时间有硬上限，绝不阻塞 AddClipboardFormatListener 的消息泵。
            if (!ClipboardRetryPolicy.Try(
                    () => WindowsNativeMethods.OpenClipboard(ownerWindow),
                    settings.OpenRetryDelays,
                    cancellationToken))
            {
                return new ClipboardWriteResult(
                    ClipboardWriteStatus.ClipboardBusy,
                    NativeErrorCode: Marshal.GetLastPInvokeError());
            }

            clipboardOpen = true;
            cancellationToken.ThrowIfCancellationRequested();

            if (!WindowsNativeMethods.EmptyClipboard())
            {
                return new ClipboardWriteResult(
                    ClipboardWriteStatus.Failed,
                    NativeErrorCode: Marshal.GetLastPInvokeError());
            }

            int successfulContentFormats = 0;
            bool anyFailure = false;

            for (int index = 0; index < allocations.Count; index++)
            {
                OwnedGlobalMemory allocation = allocations[index];
                nint result = WindowsNativeMethods.SetClipboardData(
                    allocation.Format,
                    allocation.Handle);
                if (result == 0)
                {
                    anyFailure = true;
                    nativeError = Marshal.GetLastPInvokeError();
                    continue;
                }

                allocation.TransferOwnershipToClipboard();
                if (allocation.IsFeedbackMarker)
                {
                    feedbackMarkerWritten = true;
                }
                else
                {
                    successfulContentFormats++;
                }
            }

            sequenceBeforeClose = WindowsNativeMethods.GetClipboardSequenceNumber();
            feedbackGuard.Remember(sequenceBeforeClose);

            status = successfulContentFormats == 0
                ? ClipboardWriteStatus.Failed
                : anyFailure || !feedbackMarkerWritten
                    ? ClipboardWriteStatus.Partial
                    : ClipboardWriteStatus.Success;
        }
        finally
        {
            if (clipboardOpen)
            {
                WindowsNativeMethods.CloseClipboard();
            }

            foreach (OwnedGlobalMemory allocation in allocations)
            {
                allocation.Dispose();
            }
        }

        uint sequenceAfterClose = WindowsNativeMethods.GetClipboardSequenceNumber();
        feedbackGuard.Remember(sequenceAfterClose);
        uint sequence = sequenceAfterClose == 0 ? sequenceBeforeClose : sequenceAfterClose;

        return new ClipboardWriteResult(
            status,
            sequence,
            feedbackMarkerWritten,
            nativeError);
    }

    private PayloadBuildResult BuildPayloads(ClipboardWriteRequest request)
    {
        List<ClipboardPayload> payloads = [];
        long remainingPayloadBytes = settings.MaximumPayloadBytes;

        bool TryReserve(long byteCount)
        {
            if (byteCount < 0 || byteCount > remainingPayloadBytes)
            {
                return false;
            }

            remainingPayloadBytes -= byteCount;
            return true;
        }

        if (!request.Html.IsEmpty)
        {
            uint format = WindowsNativeMethods.RegisterClipboardFormat("HTML Format");
            if (format == 0)
            {
                return new PayloadBuildResult([], Marshal.GetLastPInvokeError(), false);
            }

            long htmlLength = request.Html.Length + (request.Html.Span[^1] == 0 ? 0L : 1L);
            if (!TryReserve(htmlLength))
            {
                return new PayloadBuildResult([], 0, true);
            }

            byte[] html = AddNullTerminator(request.Html.Span);
            payloads.Add(new ClipboardPayload(format, html, false));
        }

        if (!request.RichText.IsEmpty)
        {
            uint format = WindowsNativeMethods.RegisterClipboardFormat("Rich Text Format");
            if (format == 0)
            {
                return new PayloadBuildResult([], Marshal.GetLastPInvokeError(), false);
            }

            long richTextLength =
                request.RichText.Length + (request.RichText.Span[^1] == 0 ? 0L : 1L);
            if (!TryReserve(richTextLength))
            {
                return new PayloadBuildResult([], 0, true);
            }

            byte[] richText = AddNullTerminator(request.RichText.Span);
            payloads.Add(new ClipboardPayload(format, richText, false));
        }

        if (request.Bitmap is not null)
        {
            if (request.Bitmap.Data.IsEmpty ||
                !TryReserve(request.Bitmap.Data.Length))
            {
                return new PayloadBuildResult([], 0, true);
            }

            uint format = request.Bitmap.Encoding switch
            {
                ClipboardBitmapEncoding.DeviceIndependentBitmap =>
                    WindowsNativeConstants.ClipboardFormatDeviceIndependentBitmap,
                ClipboardBitmapEncoding.DeviceIndependentBitmapV5 =>
                    WindowsNativeConstants.ClipboardFormatDeviceIndependentBitmapV5,
                _ => 0,
            };
            if (format == 0)
            {
                // PNG/TIFF 是 macOS 的编码数据，不能伪装成 Windows DIB 写入。
                // 后续若要跨平台转码，应由独立的图片编解码服务显式完成。
                return new PayloadBuildResult([], 0, true);
            }

            payloads.Add(new ClipboardPayload(format, request.Bitmap.Data.ToArray(), false));
        }

        if (request.FilePaths.Count > 0)
        {
            if (request.FilePaths.Count > settings.MaximumFileCount ||
                request.FilePaths.Any(string.IsNullOrWhiteSpace))
            {
                return new PayloadBuildResult([], 0, true);
            }

            long pathCharacterCount = 1;
            foreach (string path in request.FilePaths)
            {
                pathCharacterCount += path.Length + 1L;
            }

            long fileDropLength = DropFilesHeaderSize + (pathCharacterCount * sizeof(char));
            if (!TryReserve(fileDropLength))
            {
                return new PayloadBuildResult([], 0, true);
            }

            byte[] fileDrop = BuildFileDrop(request.FilePaths);
            payloads.Add(new ClipboardPayload(
                WindowsNativeConstants.ClipboardFormatFileDrop,
                fileDrop,
                false));
        }

        if (request.Text is not null)
        {
            long textLength = ((long)request.Text.Length + 1) * sizeof(char);
            if (!TryReserve(textLength))
            {
                return new PayloadBuildResult([], 0, true);
            }

            byte[] text = Encoding.Unicode.GetBytes(request.Text + '\0');
            payloads.Add(new ClipboardPayload(
                WindowsNativeConstants.ClipboardFormatUnicodeText,
                text,
                false));
        }

        uint markerFormat = originMarker.GetFormatId();
        if (markerFormat == 0)
        {
            return new PayloadBuildResult([], Marshal.GetLastPInvokeError(), false);
        }

        if (!TryReserve(originMarker.Payload.Length))
        {
            return new PayloadBuildResult([], 0, true);
        }

        // 来源标记放在业务格式之后，避免改变目标应用优先选择的富文本/图片格式顺序。
        payloads.Add(new ClipboardPayload(
            markerFormat,
            originMarker.Payload.ToArray(),
            true));

        return new PayloadBuildResult(payloads, 0, false);
    }

    private static byte[] BuildFileDrop(IReadOnlyList<string> filePaths)
    {
        string pathBlock = string.Join('\0', filePaths) + "\0\0";
        byte[] pathBytes = Encoding.Unicode.GetBytes(pathBlock);
        byte[] data = new byte[DropFilesHeaderSize + pathBytes.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(data, DropFilesHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1);
        pathBytes.CopyTo(data, DropFilesHeaderSize);
        return data;
    }

    private static byte[] AddNullTerminator(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty && data[^1] == 0)
        {
            return data.ToArray();
        }

        byte[] terminated = new byte[data.Length + 1];
        data.CopyTo(terminated);
        return terminated;
    }

    private sealed record ClipboardPayload(uint Format, byte[] Data, bool IsFeedbackMarker);

    private sealed record PayloadBuildResult(
        IReadOnlyList<ClipboardPayload> Payloads,
        int ErrorCode,
        bool InvalidContent);

    private sealed class OwnedGlobalMemory : IDisposable
    {
        private nint _handle;

        private OwnedGlobalMemory(ClipboardPayload payload, nint handle)
        {
            Format = payload.Format;
            IsFeedbackMarker = payload.IsFeedbackMarker;
            _handle = handle;
        }

        public uint Format { get; }

        public bool IsFeedbackMarker { get; }

        public nint Handle => _handle;

        public static OwnedGlobalMemory? TryCreate(ClipboardPayload payload)
        {
            nint handle = WindowsNativeMethods.GlobalAlloc(
                WindowsNativeConstants.GlobalMemoryMoveable |
                WindowsNativeConstants.GlobalMemoryZeroInitialize,
                (nuint)payload.Data.Length);
            if (handle == 0)
            {
                return null;
            }

            nint memory = WindowsNativeMethods.GlobalLock(handle);
            if (memory == 0)
            {
                WindowsNativeMethods.GlobalFree(handle);
                return null;
            }

            try
            {
                Marshal.Copy(payload.Data, 0, memory, payload.Data.Length);
            }
            finally
            {
                WindowsNativeMethods.GlobalUnlock(handle);
            }

            return new OwnedGlobalMemory(payload, handle);
        }

        public void TransferOwnershipToClipboard() => _handle = 0;

        public void Dispose()
        {
            nint handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0)
            {
                WindowsNativeMethods.GlobalFree(handle);
            }
        }
    }
}
