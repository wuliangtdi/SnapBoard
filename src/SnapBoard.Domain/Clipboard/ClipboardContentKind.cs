namespace SnapBoard.Domain.Clipboard;

/// <summary>
/// SnapBoard 在领域层识别的剪贴板内容类型。
/// 文件引用会保存在本机历史中，但首版同步协议不会传输文件本体。
/// </summary>
public enum ClipboardContentKind
{
    Text = 1,
    Html = 2,
    RichText = 3,
    Image = 4,
    FileReference = 5,
}
