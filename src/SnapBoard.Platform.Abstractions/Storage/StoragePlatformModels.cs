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

public sealed record StoragePathInspection(
    string CanonicalPath,
    string VolumeIdentity,
    StorageVolumeKind VolumeKind,
    string FileSystemName,
    long AvailableBytes,
    bool ContainsReparsePoint,
    bool IsPrivateToCurrentUser,
    bool SupportsWriteThroughAndAtomicRename);

public sealed record StorageProcessIdentity(
    int ProcessId,
    long StartTimeUtcTicks,
    string ExecutablePath,
    string UserIdentity);
