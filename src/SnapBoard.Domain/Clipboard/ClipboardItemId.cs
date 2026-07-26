namespace SnapBoard.Domain.Clipboard;

/// <summary>
/// 剪贴板记录的稳定标识。使用 UUIDv7 兼顾跨设备唯一性和按时间排序能力。
/// </summary>
public readonly record struct ClipboardItemId
{
    public ClipboardItemId(Guid value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty);
        Value = value;
    }

    public Guid Value { get; }

    public static ClipboardItemId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
