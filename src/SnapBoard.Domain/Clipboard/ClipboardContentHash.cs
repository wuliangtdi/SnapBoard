namespace SnapBoard.Domain.Clipboard;

/// <summary>
/// 规范化剪贴板内容的 SHA-256 标识。字符串固定为 64 位小写十六进制，
/// 便于 SQLite 索引、Blob 路径和跨设备协议后续复用。
/// </summary>
public readonly record struct ClipboardContentHash
{
    public ClipboardContentHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character =>
            character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Content hash must be 64 lowercase hexadecimal characters.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
