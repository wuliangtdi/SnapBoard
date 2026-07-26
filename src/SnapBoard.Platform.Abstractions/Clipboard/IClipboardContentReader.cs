namespace SnapBoard.Platform.Abstractions.Clipboard;

/// <summary>
/// 在平台消息回调之外读取当前剪贴板内容。实现必须限制重试和载荷大小，
/// 并将权限、占用和延迟渲染失败转换为结构化结果。
/// </summary>
public interface IClipboardContentReader
{
    ValueTask<ClipboardReadResult> ReadAsync(
        ClipboardChangedEvent change,
        CancellationToken cancellationToken);
}
