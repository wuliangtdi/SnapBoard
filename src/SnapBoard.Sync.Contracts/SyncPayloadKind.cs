namespace SnapBoard.Sync.Contracts;

/// <summary>
/// 第一版允许进入远端加密载荷的类型。这里有意不包含文件内容。
/// </summary>
public enum SyncPayloadKind
{
    Text = 1,
    Html = 2,
    RichText = 3,
    Image = 4,
}
