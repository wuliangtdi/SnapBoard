namespace SnapBoard.Platform.Abstractions.Clipboard;

public sealed record ClipboardSourceApplicationIcon(
    int Width,
    int Height,
    int Stride,
    ReadOnlyMemory<byte> BgraPixels);

public sealed record ClipboardSourceApplicationMetadata(
    string DisplayName,
    ClipboardSourceApplicationIcon? Icon = null);

public sealed record ClipboardSourceApplicationIdentity(
    string ProcessName,
    string? ExecutablePath = null,
    string? ApplicationUserModelId = null,
    string? PackageFamilyName = null);

public interface IClipboardSourceApplicationMetadataResolver
{
    ValueTask<ClipboardSourceApplicationMetadata> ResolveAsync(
        ClipboardSourceApplicationIdentity identity,
        CancellationToken cancellationToken);
}
