namespace SnapBoard.Sync.Contracts;

public enum SyncObjectType
{
    Metadata = 1,
    Event = 2,
    Blob = 3,
    Checkpoint = 4,
    ProviderMigration = 5,
}
