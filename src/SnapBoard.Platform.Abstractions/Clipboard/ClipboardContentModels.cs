namespace SnapBoard.Platform.Abstractions.Clipboard;

public enum ClipboardSourceAccessStatus
{
    Unknown = 0,
    Identified = 1,
    PathUnavailable = 2,
    AccessDenied = 3,
}

public sealed record ClipboardSourceInfo(
    int? ProcessId,
    string? ProcessName,
    string? ExecutablePath,
    ClipboardSourceAccessStatus AccessStatus);

public sealed record ClipboardFormatDescriptor(
    string Identifier,
    string Name);

public enum ClipboardBitmapEncoding
{
    DeviceIndependentBitmap = 1,
    DeviceIndependentBitmapV5 = 2,
}

public sealed record ClipboardBitmapData(
    ClipboardBitmapEncoding Encoding,
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    ushort BitsPerPixel);

public sealed class ClipboardContentSnapshot
{
    public required ulong SequenceNumber { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    public required ClipboardSourceInfo Source { get; init; }

    public IReadOnlyList<ClipboardFormatDescriptor> Formats { get; init; }
        = Array.Empty<ClipboardFormatDescriptor>();

    public IReadOnlyList<string> UnavailableFormats { get; init; }
        = Array.Empty<string>();

    public string? Text { get; init; }

    public ReadOnlyMemory<byte> Html { get; init; }

    public ReadOnlyMemory<byte> RichText { get; init; }

    public ClipboardBitmapData? Bitmap { get; init; }

    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();

    public bool IsFromCurrentApplication { get; init; }
}

public enum ClipboardReadStatus
{
    Success = 0,
    Partial = 1,
    ClipboardBusy = 2,
    Failed = 3,
}

public enum ClipboardReadFailureReason
{
    None = 0,
    ClipboardBusy = 1,
    DelayedRenderingUnavailable = 2,
    ContentTooLarge = 3,
    NativeFailure = 4,
}

public sealed record ClipboardReadResult(
    ClipboardReadStatus Status,
    ClipboardContentSnapshot? Snapshot,
    ClipboardReadFailureReason FailureReason = ClipboardReadFailureReason.None,
    int NativeErrorCode = 0);

public sealed class ClipboardWriteRequest
{
    public string? Text { get; init; }

    public ReadOnlyMemory<byte> Html { get; init; }

    public ReadOnlyMemory<byte> RichText { get; init; }

    public ClipboardBitmapData? Bitmap { get; init; }

    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();

    public bool HasContent =>
        Text is not null ||
        !Html.IsEmpty ||
        !RichText.IsEmpty ||
        Bitmap is not null ||
        FilePaths.Count > 0;

    public static ClipboardWriteRequest FromSnapshot(ClipboardContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ClipboardWriteRequest
        {
            Text = snapshot.Text,
            Html = snapshot.Html,
            RichText = snapshot.RichText,
            Bitmap = snapshot.Bitmap,
            FilePaths = snapshot.FilePaths,
        };
    }
}

public enum ClipboardWriteStatus
{
    Success = 0,
    Partial = 1,
    ClipboardBusy = 2,
    InvalidContent = 3,
    Failed = 4,
}

public sealed record ClipboardWriteResult(
    ClipboardWriteStatus Status,
    ulong SequenceNumber = 0,
    bool FeedbackMarkerWritten = false,
    int NativeErrorCode = 0);
