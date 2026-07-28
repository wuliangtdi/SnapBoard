using System.Runtime.InteropServices;

namespace SnapBoard.Platform.MacOS.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct MacOSTimespec
{
    public long Seconds;
    public long Nanoseconds;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MacOSFileStatus
{
    public int Device;
    public ushort Mode;
    public ushort LinkCount;
    public ulong Inode;
    public uint UserId;
    public uint GroupId;
    public int RawDevice;
    public MacOSTimespec AccessTime;
    public MacOSTimespec ModificationTime;
    public MacOSTimespec ChangeTime;
    public MacOSTimespec BirthTime;
    public long Size;
    public long Blocks;
    public int BlockSize;
    public uint Flags;
    public uint Generation;
    public int Spare;
    public fixed long Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MacOSFileSystemStatus
{
    public uint BlockSize;
    public int IoSize;
    public ulong Blocks;
    public ulong FreeBlocks;
    public ulong AvailableBlocks;
    public ulong Files;
    public ulong FreeFiles;
    public int FileSystemIdFirst;
    public int FileSystemIdSecond;
    public uint Owner;
    public uint Type;
    public uint Flags;
    public uint Subtype;
    public fixed byte FileSystemType[16];
    public fixed byte MountPoint[1024];
    public fixed byte MountedFrom[1024];
    public uint ExtendedFlags;
    public fixed uint Reserved[7];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MacOSProcessBsdInfo
{
    public uint Flags;
    public uint Status;
    public uint ExitStatus;
    public uint ProcessId;
    public uint ParentProcessId;
    public uint UserId;
    public uint GroupId;
    public uint RealUserId;
    public uint RealGroupId;
    public uint SavedUserId;
    public uint SavedGroupId;
    public uint Reserved;
    public fixed byte Command[16];
    public fixed byte Name[32];
    public uint OpenFileCount;
    public uint ProcessGroupId;
    public uint JobControlCount;
    public uint ControllingTerminalDevice;
    public uint ControllingTerminalProcessGroupId;
    public int Nice;
    public ulong StartTimeSeconds;
    public ulong StartTimeMicroseconds;
}

internal static partial class MacOSNativeMethods
{
    private const string LibProcess = "/usr/lib/libproc.dylib";

    [LibraryImport(
        LibSystem,
        EntryPoint = "lstat",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int LStat(string path, out MacOSFileStatus status);

    [LibraryImport(
        LibSystem,
        EntryPoint = "statfs",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int StatFs(string path, out MacOSFileSystemStatus status);

    [LibraryImport(
        LibSystem,
        EntryPoint = "realpath",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint RealPath(string path, nint resolvedPath);

    [LibraryImport(
        LibSystem,
        EntryPoint = "chmod",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ChangeMode(string path, ushort mode);

    [LibraryImport(
        LibSystem,
        EntryPoint = "mkdir",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int CreateDirectory(string path, ushort mode);

    [LibraryImport(LibSystem, EntryPoint = "geteuid")]
    internal static partial uint GetEffectiveUserId();

    [LibraryImport(
        LibSystem,
        EntryPoint = "acl_get_file",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint AclGetFile(string path, int type);

    [LibraryImport(LibSystem, EntryPoint = "acl_get_entry", SetLastError = true)]
    internal static partial int AclGetEntry(nint acl, int entryId, out nint entry);

    [LibraryImport(LibSystem, EntryPoint = "acl_get_tag_type", SetLastError = true)]
    internal static partial int AclGetTagType(nint entry, out int tagType);

    [LibraryImport(LibSystem, EntryPoint = "acl_init", SetLastError = true)]
    internal static partial nint AclInit(int entryCount);

    [LibraryImport(
        LibSystem,
        EntryPoint = "acl_set_file",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int AclSetFile(string path, int type, nint acl);

    [LibraryImport(LibSystem, EntryPoint = "acl_free", SetLastError = true)]
    internal static partial int AclFree(nint value);

    [LibraryImport(LibSystem, EntryPoint = "free")]
    internal static partial void Free(nint value);

    [LibraryImport(LibProcess, EntryPoint = "proc_pidinfo", SetLastError = true)]
    internal static partial int ProcessIdInfo(
        int processId,
        int flavor,
        ulong argument,
        ref MacOSProcessBsdInfo buffer,
        int bufferSize);

    [LibraryImport(LibProcess, EntryPoint = "proc_pidpath", SetLastError = true)]
    internal static unsafe partial int ProcessIdPath(
        int processId,
        byte* buffer,
        uint bufferSize);

    [LibraryImport(LibSystem, EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int processId, int signal);
}
