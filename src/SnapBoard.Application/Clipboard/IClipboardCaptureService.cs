namespace SnapBoard.Application.Clipboard;

/// <summary>
/// 负责运行剪贴板采集用例。平台回调、过滤、持久化和同步 Outbox
/// 将在此用例背后串联，UI 不直接接触任何原生剪贴板 API。
/// </summary>
public interface IClipboardCaptureService
{
    Task RunAsync(CancellationToken cancellationToken);
}
