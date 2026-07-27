namespace SnapBoard.Platform.Abstractions.Clipboard;

public sealed record ClipboardSourceApplicationIcon(
    int Width,
    int Height,
    int Stride,
    ReadOnlyMemory<byte> BgraPixels);

public sealed record ClipboardSourceApplicationMetadata(
    string DisplayName,
    ClipboardSourceApplicationIcon? Icon = null);

public interface IClipboardSourceApplicationMetadataResolver
{
    ValueTask<ClipboardSourceApplicationMetadata> ResolveAsync(
        string processName,
        string? executablePath,
        CancellationToken cancellationToken);
}
