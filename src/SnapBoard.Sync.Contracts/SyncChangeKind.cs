namespace SnapBoard.Sync.Contracts;

public enum SyncChangeKind
{
    Upsert = 1,
    SetTags = 2,
    SetPinned = 3,
    Delete = 4,
    Restore = 5,
    SetSetting = 6,
}
