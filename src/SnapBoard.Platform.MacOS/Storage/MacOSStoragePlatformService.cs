using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SnapBoard.Platform.Abstractions.Storage;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Storage;

internal sealed record MacOSVolumeMetadata(
    string? VolumeUuid,
    string? FileSystemType,
    string? FileSystemName,
    bool? Internal,
    bool Removable,
    bool Ejectable,
    bool Writable,
    bool? CaseSensitive);

[SupportedOSPlatform("macos")]
public sealed class MacOSStoragePlatformService : IStoragePlatformService
{
    private const int AccessControlEntryFirst = 0;
    private const int AccessControlEntryNext = -1;
    private const int AccessControlExtendedAllow = 1;
    private const int AccessControlTypeExtended = 0x100;
    private const int ErrorNoEntry = 2;
    private const int ErrorAttributeNotFound = 93;
    private const int ErrorProcessNotFound = 3;
    private const int ErrorAlreadyExists = 17;
    private const uint MountLocal = 0x00001000;
    private const uint MountReadOnly = 0x00000001;
    private const uint MountRemovable = 0x00000200;
    private const uint MountExtendedRootDataVolume = 0x00000001;
    private const int ProcessBsdInfoFlavor = 3;
    private const int ProcessPathBufferSize = 4096;
    private const int SignalTerminate = 15;
    private const ushort SymbolicLinkFileType = 0xa000;
    private const ushort FileTypeMask = 0xf000;
    private const ushort DirectoryFileType = 0x4000;
    private const ushort PrivateDirectoryMode = 0x01c0;
    private const ushort GroupAndOtherPermissionMask = 0x003f;
    private readonly Func<string, bool>? _directoryOpener;
    private readonly Func<string, MacOSVolumeMetadata> _volumeMetadataProvider;

    public MacOSStoragePlatformService()
        : this(ReadVolumeMetadata, directoryOpener: null)
    {
    }

    internal MacOSStoragePlatformService(
        Func<string, MacOSVolumeMetadata> volumeMetadataProvider,
        Func<string, bool>? directoryOpener)
    {
        _volumeMetadataProvider = volumeMetadataProvider ??
            throw new ArgumentNullException(nameof(volumeMetadataProvider));
        _directoryOpener = directoryOpener;
    }

    public ValueTask<StoragePathInspection> InspectPathAsync(
        string path,
        bool probeWriteCapabilities,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ValueTask<StoragePathInspection>(Task.Run(
            () => InspectPath(path, probeWriteCapabilities, cancellationToken),
            cancellationToken));
    }

    public ValueTask EnsurePrivateDirectoryAsync(
        string path,
        CancellationToken cancellationToken) => EnsurePrivateDirectoryAsync(
        path,
        StorageDirectorySecurityMode.EmptyDirectoryOnly,
        cancellationToken);

    public ValueTask EnsurePrivateDirectoryAsync(
        string path,
        StorageDirectorySecurityMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ValueTask(Task.Run(
            () => EnsurePrivateDirectory(path, mode, cancellationToken),
            cancellationToken));
    }

    public StoragePathRelation GetPathRelation(string left, string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        PathFacts leftFacts = GetPathFacts(left);
        PathFacts rightFacts = GetPathFacts(right);

        if (leftFacts.Exists && rightFacts.Exists &&
            leftFacts.Device == rightFacts.Device &&
            leftFacts.Inode == rightFacts.Inode)
        {
            return StoragePathRelation.Same;
        }

        bool caseSensitive = leftFacts.CaseSensitive && rightFacts.CaseSensitive;
        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        string normalizedLeft = NormalizeForComparison(leftFacts.CanonicalPath);
        string normalizedRight = NormalizeForComparison(rightFacts.CanonicalPath);
        if (normalizedLeft.Equals(normalizedRight, comparison))
        {
            return StoragePathRelation.Same;
        }

        if (normalizedRight.StartsWith(
                AppendDirectorySeparator(normalizedLeft),
                comparison))
        {
            return StoragePathRelation.Ancestor;
        }

        return normalizedLeft.StartsWith(
            AppendDirectorySeparator(normalizedRight),
            comparison)
            ? StoragePathRelation.Descendant
            : StoragePathRelation.Unrelated;
    }

    public StorageProcessIdentity GetCurrentProcessIdentity() =>
        ReadProcessIdentity(Environment.ProcessId) ?? throw new InvalidOperationException(
            "The current macOS process identity is unavailable.");

    public async ValueTask WaitForProcessExitAsync(
        StorageProcessIdentity process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        EnsureCurrentUser(process.UserIdentity);
        Process candidate;
        try
        {
            candidate = Process.GetProcessById(process.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (candidate)
        {
            StorageProcessIdentity? actual = ReadProcessIdentity(process.ProcessId);
            if (actual is null)
            {
                return;
            }

            EnsureMatchingProcess(process, actual, "The migration parent process identity changed.");
            await candidate.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask<StorageProcessIdentity> StartProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        string canonicalExecutable = CanonicalizePath(executablePath);
        if (!File.Exists(canonicalExecutable))
        {
            throw new FileNotFoundException("The process executable is unavailable.", canonicalExecutable);
        }

        ProcessStartInfo startInfo = new(canonicalExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(canonicalExecutable) ?? AppContext.BaseDirectory,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process started = Process.Start(startInfo) ??
            throw new InvalidOperationException("The process could not be started.");
        StorageProcessIdentity identity = ReadProcessIdentity(started.Id) ??
            throw new InvalidOperationException("The started process identity is unavailable.");
        return ValueTask.FromResult(identity);
    }

    public async ValueTask StopProcessAsync(
        StorageProcessIdentity process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        EnsureCurrentUser(process.UserIdentity);
        Process candidate;
        try
        {
            candidate = Process.GetProcessById(process.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (candidate)
        {
            StorageProcessIdentity? actual = ReadProcessIdentity(process.ProcessId);
            if (actual is null)
            {
                return;
            }

            EnsureMatchingProcess(
                process,
                actual,
                "The process selected for termination does not match migration state.");
            if (MacOSNativeMethods.Kill(process.ProcessId, SignalTerminate) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == ErrorProcessNotFound)
                {
                    return;
                }

                throw new Win32Exception(error, "The migration helper could not be stopped.");
            }

            await candidate.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public bool OpenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string canonicalPath;
        try
        {
            canonicalPath = CanonicalizePath(path);
        }
        catch (IOException)
        {
            return false;
        }

        if (!Directory.Exists(canonicalPath))
        {
            return false;
        }

        try
        {
            return _directoryOpener?.Invoke(canonicalPath) ?? OpenDirectoryWithWorkspace(canonicalPath);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static StorageVolumeKind ClassifyVolume(
        MacOSFileSystemStatus fileSystem,
        MacOSVolumeMetadata metadata) => ClassifyVolume(
        ReadFileSystemType(fileSystem),
        fileSystem.Flags,
        fileSystem.ExtendedFlags,
        metadata);

    internal static StorageVolumeKind ClassifyVolume(
        string fileSystemType,
        uint mountFlags,
        uint extendedMountFlags,
        MacOSVolumeMetadata metadata)
    {
        if ((mountFlags & MountLocal) == 0)
        {
            return StorageVolumeKind.Network;
        }

        if (fileSystemType is "tmpfs" or "ramfs")
        {
            return StorageVolumeKind.Ram;
        }

        if (fileSystemType is "cd9660" or "udf")
        {
            return StorageVolumeKind.Optical;
        }

        bool rootDataVolume = (extendedMountFlags & MountExtendedRootDataVolume) != 0;
        bool readOnly = (mountFlags & MountReadOnly) != 0 || !metadata.Writable;
        if (readOnly)
        {
            return StorageVolumeKind.Unknown;
        }

        bool removable = (mountFlags & MountRemovable) != 0 ||
            metadata.Removable ||
            metadata.Ejectable ||
            metadata.Internal == false;
        if (removable && !rootDataVolume)
        {
            return StorageVolumeKind.Removable;
        }

        string effectiveFileSystemType = string.IsNullOrWhiteSpace(fileSystemType)
            ? metadata.FileSystemType ?? string.Empty
            : fileSystemType;
        bool supportedFileSystem = string.Equals(
            effectiveFileSystemType,
            "apfs",
            StringComparison.OrdinalIgnoreCase);
        bool stableIdentityAvailable = !string.IsNullOrWhiteSpace(metadata.VolumeUuid);
        return supportedFileSystem && stableIdentityAvailable &&
            (metadata.Internal == true || rootDataVolume)
            ? StorageVolumeKind.Fixed
            : StorageVolumeKind.Unknown;
    }

    private StoragePathInspection InspectPath(
        string path,
        bool probeWriteCapabilities,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string lexicalPath = GetLexicalAbsolutePath(path);
        bool containsSymbolicLink = ContainsSymbolicLink(lexicalPath);
        string canonicalPath = CanonicalizePath(lexicalPath);
        string existingDirectory = FindNearestExistingDirectory(canonicalPath);
        MacOSFileSystemStatus fileSystem = GetFileSystemStatus(existingDirectory);
        string mountPoint = ReadMountPoint(fileSystem);
        MacOSVolumeMetadata metadata = _volumeMetadataProvider(mountPoint);
        MacOSFileStatus? targetStatus = TryGetFileStatus(canonicalPath, out MacOSFileStatus status)
            ? status
            : null;
        bool isPrivate = targetStatus.HasValue && Directory.Exists(canonicalPath) &&
            IsPrivateDirectory(canonicalPath, targetStatus.Value);
        bool probeSucceeded = !probeWriteCapabilities || ProbeWriteCapabilities(canonicalPath);
        long availableBytes = GetAvailableBytes(fileSystem);
        string volumeIdentity = !string.IsNullOrWhiteSpace(metadata.VolumeUuid)
            ? $"uuid:{metadata.VolumeUuid.ToUpperInvariant()}"
            : FormattableString.Invariant(
                $"fsid:{fileSystem.FileSystemIdFirst:x8}:{fileSystem.FileSystemIdSecond:x8}");
        string fileIdentity = targetStatus.HasValue
            ? FormattableString.Invariant(
                $"{targetStatus.Value.Device:x8}:{targetStatus.Value.Inode:x16}")
            : string.Empty;

        return new StoragePathInspection(
            canonicalPath,
            volumeIdentity,
            ClassifyVolume(fileSystem, metadata),
            ReadFileSystemType(fileSystem),
            availableBytes,
            containsSymbolicLink,
            isPrivate,
            probeSucceeded,
            fileIdentity,
            metadata.CaseSensitive ?? true);
    }

    private static void EnsurePrivateDirectory(
        string path,
        StorageDirectorySecurityMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string lexicalPath = GetLexicalAbsolutePath(path);
        if (ContainsSymbolicLink(lexicalPath))
        {
            throw new IOException("The private directory path contains a symbolic link.");
        }

        string canonicalPath = Path.GetFullPath(lexicalPath);
        CreateMissingDirectories(canonicalPath, cancellationToken);
        if (!TryGetFileStatus(canonicalPath, out MacOSFileStatus status) ||
            (status.Mode & FileTypeMask) != DirectoryFileType)
        {
            throw new IOException("The private storage path is not a directory.");
        }

        if (status.UserId != MacOSNativeMethods.GetEffectiveUserId())
        {
            throw new UnauthorizedAccessException("The private storage directory has another owner.");
        }

        if (IsPrivateDirectory(canonicalPath, status))
        {
            return;
        }

        if (mode == StorageDirectorySecurityMode.EmptyDirectoryOnly &&
            Directory.EnumerateFileSystemEntries(canonicalPath).Any())
        {
            throw new InvalidOperationException(
                "A non-empty user directory cannot be modified during validation.");
        }

        if (MacOSNativeMethods.ChangeMode(canonicalPath, PrivateDirectoryMode) != 0)
        {
            throw CreateNativeException("The private storage directory mode could not be updated.");
        }

        ClearExtendedAccessControlList(canonicalPath);
        if (ContainsSymbolicLink(canonicalPath) ||
            !TryGetFileStatus(canonicalPath, out status) ||
            !IsPrivateDirectory(canonicalPath, status))
        {
            throw new UnauthorizedAccessException(
                "The private storage directory permissions could not be verified.");
        }
    }

    private PathFacts GetPathFacts(string path)
    {
        string lexicalPath = GetLexicalAbsolutePath(path);
        string canonicalPath = CanonicalizePath(lexicalPath);
        bool exists = TryGetFileStatus(canonicalPath, out MacOSFileStatus status);
        string existingDirectory = Directory.Exists(canonicalPath)
            ? canonicalPath
            : FindNearestExistingDirectory(canonicalPath);
        MacOSFileSystemStatus fileSystem = GetFileSystemStatus(existingDirectory);
        MacOSVolumeMetadata metadata = _volumeMetadataProvider(ReadMountPoint(fileSystem));
        return new PathFacts(
            canonicalPath,
            exists,
            exists ? status.Device : 0,
            exists ? status.Inode : 0,
            metadata.CaseSensitive ?? true);
    }

    private static void CreateMissingDirectories(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        List<string> missing = [];
        string? current = canonicalPath;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new IOException("A storage directory component is a file.");
            }

            missing.Add(current);
            current = Path.GetDirectoryName(current);
        }

        if (string.IsNullOrEmpty(current))
        {
            throw new DirectoryNotFoundException("No existing storage path ancestor was found.");
        }

        for (int index = missing.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = missing[index];
            if (MacOSNativeMethods.CreateDirectory(directory, PrivateDirectoryMode) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != ErrorAlreadyExists)
                {
                    throw new Win32Exception(error, "A private storage directory could not be created.");
                }
            }

            if (ContainsSymbolicLink(directory))
            {
                throw new IOException("A newly created storage path contains a symbolic link.");
            }
        }
    }

    private static void ClearExtendedAccessControlList(string path)
    {
        nint emptyAcl = MacOSNativeMethods.AclInit(0);
        if (emptyAcl == 0)
        {
            throw CreateNativeException("An empty access control list could not be created.");
        }

        try
        {
            if (MacOSNativeMethods.AclSetFile(path, AccessControlTypeExtended, emptyAcl) != 0)
            {
                throw CreateNativeException("The extended access control list could not be cleared.");
            }
        }
        finally
        {
            _ = MacOSNativeMethods.AclFree(emptyAcl);
        }
    }

    private static bool IsPrivateDirectory(string path, MacOSFileStatus status)
    {
        if (status.UserId != MacOSNativeMethods.GetEffectiveUserId() ||
            (status.Mode & GroupAndOtherPermissionMask) != 0)
        {
            return false;
        }

        nint acl = MacOSNativeMethods.AclGetFile(path, AccessControlTypeExtended);
        if (acl == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 0 or ErrorNoEntry or ErrorAttributeNotFound)
            {
                return true;
            }

            throw CreateNativeException("The directory access control list could not be inspected.");
        }

        try
        {
            int entryId = AccessControlEntryFirst;
            while (true)
            {
                int result = MacOSNativeMethods.AclGetEntry(acl, entryId, out nint entry);
                if (result == 0)
                {
                    return true;
                }

                if (result < 0)
                {
                    throw CreateNativeException("The directory access control list is invalid.");
                }

                if (MacOSNativeMethods.AclGetTagType(entry, out int tagType) != 0)
                {
                    throw CreateNativeException("An access control list entry could not be inspected.");
                }

                // POSIX owner permissions already cover the current user. Any extended allow entry
                // can grant another principal access, so the private-root policy rejects it.
                if (tagType == AccessControlExtendedAllow)
                {
                    return false;
                }

                entryId = AccessControlEntryNext;
            }
        }
        finally
        {
            _ = MacOSNativeMethods.AclFree(acl);
        }
    }

    private static bool ProbeWriteCapabilities(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        string identifier = Guid.NewGuid().ToString("N");
        string sourcePath = Path.Combine(path, $".snapboard-probe-{identifier}.tmp");
        string destinationPath = Path.Combine(path, $".snapboard-probe-{identifier}.moved");
        try
        {
            using (FileStream stream = new(
                sourcePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.WriteByte(0x53);
                stream.Flush(flushToDisk: true);
            }

            File.Move(sourcePath, destinationPath);
            File.Delete(destinationPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(destinationPath);
        }
    }

    private static StorageProcessIdentity? ReadProcessIdentity(int processId)
    {
        MacOSProcessBsdInfo info = default;
        int infoSize = Marshal.SizeOf<MacOSProcessBsdInfo>();
        int bytes = MacOSNativeMethods.ProcessIdInfo(
            processId,
            ProcessBsdInfoFlavor,
            argument: 0,
            ref info,
            infoSize);
        if (bytes <= 0)
        {
            return null;
        }

        string executablePath = ReadProcessPath(processId);
        long startTicks = checked(
            DateTimeOffset.FromUnixTimeSeconds((long)info.StartTimeSeconds).UtcTicks +
            ((long)info.StartTimeMicroseconds * TimeSpan.TicksPerMicrosecond));
        return new StorageProcessIdentity(
            processId,
            startTicks,
            CanonicalizePath(executablePath),
            info.UserId.ToString(CultureInfo.InvariantCulture));
    }

    private static unsafe string ReadProcessPath(int processId)
    {
        byte[] buffer = new byte[ProcessPathBufferSize];
        fixed (byte* pointer = buffer)
        {
            int length = MacOSNativeMethods.ProcessIdPath(
                processId,
                pointer,
                (uint)buffer.Length);
            if (length <= 0)
            {
                throw CreateNativeException("The process executable path is unavailable.");
            }

            int terminator = Array.IndexOf(buffer, (byte)0, 0, Math.Min(length, buffer.Length));
            int textLength = terminator >= 0 ? terminator : Math.Min(length, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, textLength);
        }
    }

    private void EnsureMatchingProcess(
        StorageProcessIdentity expected,
        StorageProcessIdentity actual,
        string errorMessage)
    {
        if (actual.ProcessId != expected.ProcessId ||
            actual.StartTimeUtcTicks != expected.StartTimeUtcTicks ||
            !string.Equals(actual.UserIdentity, expected.UserIdentity, StringComparison.Ordinal) ||
            GetPathRelation(actual.ExecutablePath, expected.ExecutablePath) != StoragePathRelation.Same)
        {
            throw new InvalidOperationException(errorMessage);
        }
    }

    private static void EnsureCurrentUser(string expectedIdentity)
    {
        string current = MacOSNativeMethods.GetEffectiveUserId().ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(current, expectedIdentity, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The migration process user does not match.");
        }
    }

    private static bool OpenDirectoryWithWorkspace(string path)
    {
        MacOSAppKit.EnsureInitialized();
        using NativeAutoreleasePool pool = new();
        nint pathText = ObjectiveC.CreateString(path);
        try
        {
            nint url = MacOSNativeMethods.SendIntPtrWithIntPtrByte(
                ObjectiveC.GetRequiredClass("NSURL"),
                ObjectiveC.GetSelector("fileURLWithPath:isDirectory:"),
                pathText,
                1);
            nint workspace = MacOSNativeMethods.SendIntPtr(
                ObjectiveC.GetRequiredClass("NSWorkspace"),
                ObjectiveC.GetSelector("sharedWorkspace"));
            return url != 0 && workspace != 0 &&
                MacOSNativeMethods.SendBoolWithIntPtr(
                    workspace,
                    ObjectiveC.GetSelector("openURL:"),
                    url) != 0;
        }
        finally
        {
            ObjectiveC.Release(pathText);
        }
    }

    private static MacOSVolumeMetadata ReadVolumeMetadata(string mountPoint)
    {
        ProcessStartInfo startInfo = new("/usr/sbin/diskutil")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("-plist");
        startInfo.ArgumentList.Add(mountPoint);
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("macOS volume metadata could not be queried.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);
        if (process.ExitCode != 0)
        {
            return new MacOSVolumeMetadata(
                null,
                null,
                null,
                null,
                Removable: false,
                Ejectable: false,
                Writable: false,
                CaseSensitive: null);
        }

        Dictionary<string, object> values = ParsePropertyList(outputTask.Result);
        string? fileSystemName = GetString(values, "FilesystemName");
        bool? caseSensitive = GetBoolean(values, "CaseSensitive") ??
            (fileSystemName?.Contains("case-sensitive", StringComparison.OrdinalIgnoreCase) == true
                ? true
                : string.Equals(
                    GetString(values, "FilesystemType"),
                    "apfs",
                    StringComparison.OrdinalIgnoreCase)
                    ? false
                    : null);
        return new MacOSVolumeMetadata(
            GetString(values, "VolumeUUID") ?? GetString(values, "DiskUUID"),
            GetString(values, "FilesystemType"),
            fileSystemName,
            GetBoolean(values, "Internal") ?? GetBoolean(values, "OSInternalMedia"),
            GetBoolean(values, "Removable") == true ||
                GetBoolean(values, "RemovableMedia") == true,
            GetBoolean(values, "Ejectable") == true,
            GetBoolean(values, "WritableVolume") ?? GetBoolean(values, "Writable") ?? false,
            caseSensitive);
    }

    private static Dictionary<string, object> ParsePropertyList(string xml)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        using StringReader text = new(xml);
        using XmlReader reader = XmlReader.Create(text, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement dictionary = document.Root?.Element("dict") ??
            throw new InvalidDataException("The macOS volume metadata is invalid.");
        XElement[] elements = dictionary.Elements().ToArray();
        Dictionary<string, object> values = new(StringComparer.Ordinal);
        for (int index = 0; index + 1 < elements.Length; index += 2)
        {
            if (elements[index].Name.LocalName != "key")
            {
                throw new InvalidDataException("The macOS volume metadata dictionary is invalid.");
            }

            XElement value = elements[index + 1];
            object? parsed = value.Name.LocalName switch
            {
                "string" => value.Value,
                "true" => true,
                "false" => false,
                "integer" when long.TryParse(
                    value.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long integer) => integer,
                _ => null,
            };
            if (parsed is not null)
            {
                values[elements[index].Value] = parsed;
            }
        }

        return values;
    }

    private static string? GetString(Dictionary<string, object> values, string key) =>
        values.TryGetValue(key, out object? value) ? value as string : null;

    private static bool? GetBoolean(Dictionary<string, object> values, string key) =>
        values.TryGetValue(key, out object? value) && value is bool boolean ? boolean : null;

    private static MacOSFileSystemStatus GetFileSystemStatus(string path)
    {
        if (MacOSNativeMethods.StatFs(path, out MacOSFileSystemStatus status) != 0)
        {
            throw CreateNativeException("The storage volume could not be inspected.");
        }

        return status;
    }

    private static bool TryGetFileStatus(string path, out MacOSFileStatus status)
    {
        if (MacOSNativeMethods.LStat(path, out status) == 0)
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorNoEntry)
        {
            return false;
        }

        throw new Win32Exception(error, "A storage path component could not be inspected.");
    }

    private static bool ContainsSymbolicLink(string path)
    {
        string absolutePath = GetLexicalAbsolutePath(path);
        List<string> segments = [];
        foreach (string segment in absolutePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
            string current = Path.DirectorySeparatorChar + string.Join(
                Path.DirectorySeparatorChar,
                segments);
            if (TryGetFileStatus(current, out MacOSFileStatus status) &&
                (status.Mode & FileTypeMask) == SymbolicLinkFileType)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetLexicalAbsolutePath(string path) => Path.IsPathFullyQualified(path)
        ? path
        : Path.Combine(Environment.CurrentDirectory, path);

    private static string CanonicalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return ResolveExistingPath(fullPath);
        }

        string existing = FindNearestExistingDirectory(fullPath);
        string resolvedExisting = ResolveExistingPath(existing);
        string relative = Path.GetRelativePath(existing, fullPath);
        return Path.GetFullPath(Path.Combine(resolvedExisting, relative));
    }

    private static string ResolveExistingPath(string path)
    {
        nint resolved = MacOSNativeMethods.RealPath(path, 0);
        if (resolved == 0)
        {
            throw CreateNativeException("The storage path could not be canonicalized.");
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved) ??
                throw new IOException("The canonical storage path is invalid.");
        }
        finally
        {
            MacOSNativeMethods.Free(resolved);
        }
    }

    private static string FindNearestExistingDirectory(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return current ?? throw new DirectoryNotFoundException(
            "No existing storage path ancestor was found.");
    }

    private static long GetAvailableBytes(MacOSFileSystemStatus status)
    {
        ulong available = status.AvailableBlocks > ulong.MaxValue / status.BlockSize
            ? ulong.MaxValue
            : status.AvailableBlocks * status.BlockSize;
        return available > long.MaxValue ? long.MaxValue : (long)available;
    }

    private static string NormalizeForComparison(string path)
    {
        string fullPath = Path.GetFullPath(path).Normalize(NormalizationForm.FormC);
        return fullPath == Path.DirectorySeparatorChar.ToString()
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string AppendDirectorySeparator(string path) =>
        path == Path.DirectorySeparatorChar.ToString()
            ? path
            : path + Path.DirectorySeparatorChar;

    private static unsafe string ReadFileSystemType(MacOSFileSystemStatus status)
    {
        byte* value = status.FileSystemType;
        return ReadNullTerminatedUtf8(value, 16).ToLowerInvariant();
    }

    private static unsafe string ReadMountPoint(MacOSFileSystemStatus status)
    {
        byte* value = status.MountPoint;
        return ReadNullTerminatedUtf8(value, 1024);
    }

    private static unsafe string ReadNullTerminatedUtf8(byte* value, int maximumLength)
    {
        int length = 0;
        while (length < maximumLength && value[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value, length));
    }

    private static Win32Exception CreateNativeException(string message) =>
        new(Marshal.GetLastPInvokeError(), message);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PathFacts(
        string CanonicalPath,
        bool Exists,
        int Device,
        ulong Inode,
        bool CaseSensitive);
}
