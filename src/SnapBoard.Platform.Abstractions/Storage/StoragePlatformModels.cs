namespace SnapBoard.Platform.Abstractions.Storage;

public enum StorageVolumeKind
{
    Unknown = 0,
    Fixed = 1,
    Removable = 2,
    Network = 3,
    Optical = 4,
    Ram = 5,
}

public enum StorageDirectorySecurityMode
{
    EmptyDirectoryOnly = 0,
    ApplicationOwnedRoot = 1,
}

public enum StoragePathRelation
{
    Unrelated = 0,
    Same = 1,
    Ancestor = 2,
    Descendant = 3,
}

public sealed record StoragePathInspection(
    string CanonicalPath,
    string VolumeIdentity,
    StorageVolumeKind VolumeKind,
    string FileSystemName,
    long AvailableBytes,
    bool ContainsReparsePoint,
    bool IsPrivateToCurrentUser,
    bool SupportsWriteThroughAndAtomicRename,
    string FileIdentity = "",
    bool IsCaseSensitive = true);

public sealed record StorageProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks,
    string ExecutablePath,
    string UserIdentity);
