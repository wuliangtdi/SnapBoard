namespace SnapBoard.Platform.Abstractions.Clipboard;

public sealed record ClipboardSourceApplicationIcon(
    int Width,
    int Height,
    int Stride,
    ReadOnlyMemory<byte> BgraPixels);

public static class ClipboardSourceApplicationIconRules
{
    public const int Width = 32;
    public const int Height = 32;
    public const int Stride = Width * 4;
    public const int ByteLength = Stride * Height;

    public static bool IsCanonical(ClipboardSourceApplicationIcon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        return icon.Width == Width &&
            icon.Height == Height &&
            icon.Stride == Stride &&
            icon.BgraPixels.Length == ByteLength;
    }
}

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

/// <summary>
/// 为新历史记录生成可持久化的规范来源应用图标。平台无法识别来源时返回 null，
/// 不能伪造成功，也不能让图标失败阻断剪贴板正文保存。
/// </summary>
public interface IClipboardSourceApplicationIconProvider
{
    ValueTask<ClipboardSourceApplicationIcon?> CaptureAsync(
        ClipboardSourceApplicationIdentity identity,
        CancellationToken cancellationToken);
}
