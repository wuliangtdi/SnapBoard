namespace SnapBoard.Platform.Abstractions.Clipboard;

/// <summary>
/// 原生剪贴板变化通知的最小信息。事件回调只应写入有界队列，
/// 不能在系统消息线程中读取大对象、访问数据库或执行网络请求。
/// </summary>
public readonly record struct ClipboardChangedEvent(
    ulong SequenceNumber,
    DateTimeOffset ObservedAt);
