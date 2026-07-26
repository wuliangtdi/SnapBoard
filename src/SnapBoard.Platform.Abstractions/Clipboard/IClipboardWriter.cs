namespace SnapBoard.Platform.Abstractions.Clipboard;

/// <summary>
/// 将历史记录写回系统剪贴板。平台实现负责附加本应用来源标记，
/// Application 和 UI 不接触原生格式或句柄。
/// </summary>
public interface IClipboardWriter
{
    ValueTask<ClipboardWriteResult> WriteAsync(
        ClipboardWriteRequest request,
        CancellationToken cancellationToken);

    ValueTask<ClipboardWriteResult> WritePlainTextAsync(
        string text,
        CancellationToken cancellationToken);
}
