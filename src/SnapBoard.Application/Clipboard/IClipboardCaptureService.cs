using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Application.Clipboard;

public enum ClipboardCaptureStatus
{
    Stored = 1,
    Merged = 2,
    Ignored = 3,
    ReadUnavailable = 4,
    Failed = 5,
}

public sealed record ClipboardCaptureResult(
    ClipboardCaptureStatus Status,
    string ReasonCode,
    ClipboardHistorySaveResult? SaveResult = null);

/// <summary>
/// 处理平台读取结果并串联过滤、规范化、哈希、持久化和保留策略。
/// 平台 watcher 仍由桌面生命周期唯一持有，避免重复消费原生事件队列。
/// </summary>
public interface IClipboardCaptureService
{
    ValueTask<ClipboardCaptureResult> ProcessAsync(
        ClipboardReadResult readResult,
        CancellationToken cancellationToken);
}
