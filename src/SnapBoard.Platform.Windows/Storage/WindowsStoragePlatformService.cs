using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SnapBoard.Platform.Abstractions.Storage;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Storage;

[SupportedOSPlatform("windows")]
public sealed class WindowsStoragePlatformService : IStoragePlatformService
{
    private const int VolumePathBufferLength = 1024;
    private static readonly FileSystemRights ExposedRights =
        FileSystemRights.ReadData |
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.Delete |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ValueTask(Task.Run(
            () => EnsurePrivateDirectory(path, cancellationToken),
            cancellationToken));
    }

    public StorageProcessIdentity GetCurrentProcessIdentity()
    {
        using Process process = Process.GetCurrentProcess();
        return CreateProcessIdentity(process);
    }

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
            StorageProcessIdentity actual = CreateProcessIdentity(candidate);
            if (actual.StartTimeUtcTicks != process.StartTimeUtcTicks ||
                !PathEquals(actual.ExecutablePath, process.ExecutablePath))
            {
                throw new InvalidOperationException("The migration parent process identity changed.");
            }

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

        string canonicalExecutable = Path.GetFullPath(executablePath);
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
        return ValueTask.FromResult(CreateProcessIdentity(started));
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
            StorageProcessIdentity actual = CreateProcessIdentity(candidate);
            if (actual.StartTimeUtcTicks != process.StartTimeUtcTicks ||
                !PathEquals(actual.ExecutablePath, process.ExecutablePath))
            {
                throw new InvalidOperationException(
                    "The process selected for termination does not match migration state.");
            }

            candidate.Kill(entireProcessTree: false);
            await candidate.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public bool OpenDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string canonicalPath = Path.GetFullPath(path);
        if (!Directory.Exists(canonicalPath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(canonicalPath)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static StoragePathInspection InspectPath(
        string path,
        bool probeWriteCapabilities,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string canonicalPath = Path.GetFullPath(path);
        string existingPath = FindNearestExistingDirectory(canonicalPath);
        DriveInfo drive = new(Path.GetPathRoot(existingPath) ?? existingPath);
        string volumeIdentity = GetStableVolumeIdentity(existingPath);
        bool containsReparsePoint = ContainsReparsePoint(existingPath);
        bool isPrivate = Directory.Exists(canonicalPath) && IsPrivateToCurrentUser(canonicalPath);
        bool probeSucceeded = !probeWriteCapabilities || ProbeWriteCapabilities(canonicalPath);

        return new StoragePathInspection(
            canonicalPath,
            volumeIdentity,
            MapDriveType(drive.DriveType),
            drive.IsReady ? drive.DriveFormat : string.Empty,
            drive.IsReady ? drive.AvailableFreeSpace : 0,
            containsReparsePoint,
            isPrivate,
            probeSucceeded);
    }

    private static void EnsurePrivateDirectory(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string canonicalPath = Path.GetFullPath(path);
        SecurityIdentifier currentUser = GetCurrentUserSid();
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        AddFullControlRule(security, currentUser);
        AddFullControlRule(security, system);
        AddFullControlRule(security, administrators);

        if (!Directory.Exists(canonicalPath))
        {
            FileSystemAclExtensions.CreateDirectory(security, canonicalPath);
        }
        else
        {
            new DirectoryInfo(canonicalPath).SetAccessControl(security);
        }
    }

    private static void AddFullControlRule(
        DirectorySecurity security,
        SecurityIdentifier identity) => security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static bool IsPrivateToCurrentUser(string path)
    {
        SecurityIdentifier currentUser = GetCurrentUserSid();
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules.Cast<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & ExposedRights) == 0 ||
                rule.IdentityReference is not SecurityIdentifier identity)
            {
                continue;
            }

            if (!identity.Equals(currentUser) &&
                !identity.Equals(system) &&
                !identity.Equals(administrators))
            {
                return false;
            }
        }

        return true;
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

    private static string FindNearestExistingDirectory(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return current ?? throw new DirectoryNotFoundException("No existing path ancestor was found.");
    }

    private static bool ContainsReparsePoint(string path)
    {
        string root = Path.GetPathRoot(path) ?? throw new InvalidOperationException("Path has no root.");
        string current = root;
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return HasReparsePoint(root);
        }

        foreach (string segment in relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (HasReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static unsafe string GetStableVolumeIdentity(string path)
    {
        Span<char> mountPoint = stackalloc char[VolumePathBufferLength];
        fixed (char* mountPointPointer = mountPoint)
        {
            if (!WindowsStorageNativeMethods.GetVolumePathName(
                    path,
                    mountPointPointer,
                    (uint)mountPoint.Length))
            {
                throw new Win32Exception();
            }
        }

        int mountPointLength = mountPoint.IndexOf('\0');
        if (mountPointLength < 0)
        {
            throw new InvalidOperationException("The volume mount point exceeded the buffer limit.");
        }

        string mountPointValue = new(mountPoint[..mountPointLength]);
        Span<char> volumeName = stackalloc char[VolumePathBufferLength];
        fixed (char* volumeNamePointer = volumeName)
        {
            if (!WindowsStorageNativeMethods.GetVolumeNameForVolumeMountPoint(
                    mountPointValue,
                    volumeNamePointer,
                    (uint)volumeName.Length))
            {
                throw new Win32Exception();
            }
        }

        int volumeNameLength = volumeName.IndexOf('\0');
        if (volumeNameLength < 0)
        {
            throw new InvalidOperationException("The volume identity exceeded the buffer limit.");
        }

        return new string(volumeName[..volumeNameLength]);
    }

    private static StorageProcessIdentity CreateProcessIdentity(Process process)
    {
        string executablePath = process.MainModule?.FileName ??
            throw new InvalidOperationException("The process executable path is unavailable.");
        return new StorageProcessIdentity(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            Path.GetFullPath(executablePath),
            GetCurrentUserSid().Value);
    }

    private static void EnsureCurrentUser(string expectedIdentity)
    {
        if (!string.Equals(
                GetCurrentUserSid().Value,
                expectedIdentity,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The migration process user does not match.");
        }
    }

    private static SecurityIdentifier GetCurrentUserSid() =>
        WindowsIdentity.GetCurrent().User ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");

    private static StorageVolumeKind MapDriveType(DriveType driveType) => driveType switch
    {
        DriveType.Fixed => StorageVolumeKind.Fixed,
        DriveType.Removable => StorageVolumeKind.Removable,
        DriveType.Network => StorageVolumeKind.Network,
        DriveType.CDRom => StorageVolumeKind.Optical,
        DriveType.Ram => StorageVolumeKind.Ram,
        _ => StorageVolumeKind.Unknown,
    };

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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
}
