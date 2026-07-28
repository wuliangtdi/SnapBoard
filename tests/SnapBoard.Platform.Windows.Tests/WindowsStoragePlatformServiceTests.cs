using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SnapBoard.Platform.Abstractions.Storage;
using SnapBoard.Platform.Windows.Storage;

namespace SnapBoard.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsStoragePlatformServiceTests
{
    [WindowsFact]
    public async Task PrivateFixedDirectoryReportsStableVolumeAndWriteCapabilities()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"SnapBoard.Windows.Storage.Tests.{Guid.NewGuid():N}");
        WindowsStoragePlatformService service = new();
        try
        {
            await service.EnsurePrivateDirectoryAsync(root, CancellationToken.None);

            StoragePathInspection inspection = await service.InspectPathAsync(
                root,
                probeWriteCapabilities: true,
                CancellationToken.None);

            Assert.Equal(Path.GetFullPath(root), inspection.CanonicalPath);
            Assert.Equal(StorageVolumeKind.Fixed, inspection.VolumeKind);
            Assert.StartsWith(@"\\?\Volume{", inspection.VolumeIdentity, StringComparison.OrdinalIgnoreCase);
            Assert.True(inspection.AvailableBytes > 0);
            Assert.False(inspection.ContainsReparsePoint);
            Assert.True(inspection.IsPrivateToCurrentUser);
            Assert.True(inspection.SupportsWriteThroughAndAtomicRename);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [WindowsFact]
    public void CurrentProcessIdentityIncludesPidStartTimeExecutableAndUserSid()
    {
        WindowsStoragePlatformService service = new();

        StorageProcessIdentity identity = service.GetCurrentProcessIdentity();

        Assert.Equal(Environment.ProcessId, identity.ProcessId);
        Assert.True(identity.StartTimeUtcTicks > 0);
        Assert.True(Path.IsPathFullyQualified(identity.ExecutablePath));
        Assert.StartsWith("S-1-", identity.UserIdentity, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task ExistingEmptyDirectoryWithBroadAclCanBeHardened()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"SnapBoard.Windows.Storage.Acl.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        WindowsStoragePlatformService service = new();
        try
        {
            DirectorySecurity security = new DirectoryInfo(root).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(root).SetAccessControl(security);

            StoragePathInspection exposed = await service.InspectPathAsync(
                root,
                probeWriteCapabilities: false,
                CancellationToken.None);
            Assert.False(exposed.IsPrivateToCurrentUser);

            await service.EnsurePrivateDirectoryAsync(root, CancellationToken.None);
            StoragePathInspection hardened = await service.InspectPathAsync(
                root,
                probeWriteCapabilities: true,
                CancellationToken.None);

            Assert.True(hardened.IsPrivateToCurrentUser);
            Assert.True(hardened.SupportsWriteThroughAndAtomicRename);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
