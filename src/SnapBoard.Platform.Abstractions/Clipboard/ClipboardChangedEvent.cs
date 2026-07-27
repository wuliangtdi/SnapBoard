namespace SnapBoard.Platform.Abstractions.Clipboard;

/// <summary>
/// 原生剪贴板变化通知的最小信息。事件回调只应写入有界队列，
/// 不能在系统消息线程中读取大对象、访问数据库或执行网络请求。
/// </summary>
public readonly record struct ClipboardChangedEvent(
    ulong SequenceNumber,
    DateTimeOffset ObservedAt,
    ClipboardSourceProcessHint SourceHint = default);

/// <summary>
/// 系统变化通知到达瞬间采集的轻量来源线索。只保存数值型进程标识，
/// 路径、包身份和图标解析必须离开原生消息线程后执行。
/// </summary>
public readonly record struct ClipboardSourceProcessHint(
    int? ClipboardOwnerProcessId = null,
    int? ForegroundProcessId = null);
