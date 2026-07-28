using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SnapBoard.Platform.Abstractions.Storage;
using SnapBoard.Platform.MacOS.Interop;
using SnapBoard.Platform.MacOS.Storage;

namespace SnapBoard.Platform.MacOS.Tests;

[SupportedOSPlatform("macos")]
[Collection(CollectionName)]
public sealed class MacOSStoragePlatformServiceTests
{
    internal const string CollectionName = "macOS storage native integration";

    [MacOSFact]
    public async Task PrivateApfsDirectoryReportsStableVolumeAndWriteCapabilities()
    {
        string root = CreateTemporaryRoot();
        try
        {
            MacOSStoragePlatformService service = new();
            await service.EnsurePrivateDirectoryAsync(
                root,
                StorageDirectorySecurityMode.ApplicationOwnedRoot,
                CancellationToken.None);

            StoragePathInspection first = await service.InspectPathAsync(
                root,
                probeWriteCapabilities: true,
                CancellationToken.None);
            StoragePathInspection second = await service.InspectPathAsync(
                root,
                probeWriteCapabilities: false,
                CancellationToken.None);

            Assert.Equal(StorageVolumeKind.Fixed, first.VolumeKind);
            Assert.Equal("apfs", first.FileSystemName);
            Assert.StartsWith("uuid:", first.VolumeIdentity, StringComparison.Ordinal);
            Assert.Equal(first.VolumeIdentity, second.VolumeIdentity);
            Assert.Equal(first.FileIdentity, second.FileIdentity);
            Assert.True(first.AvailableBytes > 0);
            Assert.False(first.ContainsReparsePoint);
            Assert.True(first.IsPrivateToCurrentUser);
            Assert.True(first.SupportsWriteThroughAndAtomicRename);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [MacOSFact]
    public async Task ExistingEmptyDirectoryWithBroadModeCanBeHardened()
    {
        string root = CreateTemporaryRoot();
        try
        {
            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            MacOSStoragePlatformService service = new();

            await service.EnsurePrivateDirectoryAsync(
                root,
                StorageDirectorySecurityMode.EmptyDirectoryOnly,
                CancellationToken.None);
            StoragePathInspection inspection = await service.InspectPathAsync(
                root,
                probeWriteCapabilities: false,
                CancellationToken.None);

            UnixFileMode mode = File.GetUnixFileMode(root);
            Assert.True(inspection.IsPrivateToCurrentUser);
            Assert.Equal(
                (UnixFileMode)0,
                mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [MacOSFact]
    public async Task NonEmptyUserDirectoryIsNotModifiedByHardening()
    {
        string root = CreateTemporaryRoot();
        string existing = Path.Combine(root, "existing.txt");
        await File.WriteAllTextAsync(existing, "keep");
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        UnixFileMode modeBefore = File.GetUnixFileMode(root);
        DateTime timestampBefore = Directory.GetLastWriteTimeUtc(root);
        MacOSStoragePlatformService service = new();

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.EnsurePrivateDirectoryAsync(
                    root,
                    StorageDirectorySecurityMode.EmptyDirectoryOnly,
                    CancellationToken.None));

            Assert.Equal(modeBefore, File.GetUnixFileMode(root));
            Assert.Equal(timestampBefore, Directory.GetLastWriteTimeUtc(root));
            Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [MacOSFact]
    public async Task TargetAndAncestorSymbolicLinksAreReportedAndRejected()
    {
        string root = CreateTemporaryRoot();
        string real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        string link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, real);
        string child = Path.Combine(link, "child");
        Directory.CreateDirectory(Path.Combine(real, "child"));
        MacOSStoragePlatformService service = new();

        try
        {
            StoragePathInspection target = await service.InspectPathAsync(
                link,
                probeWriteCapabilities: false,
                CancellationToken.None);
            StoragePathInspection ancestor = await service.InspectPathAsync(
                child,
                probeWriteCapabilities: false,
                CancellationToken.None);

            Assert.True(target.ContainsReparsePoint);
            Assert.True(ancestor.ContainsReparsePoint);
            await Assert.ThrowsAsync<IOException>(async () =>
                await service.EnsurePrivateDirectoryAsync(link, CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(async () =>
                await service.EnsurePrivateDirectoryAsync(child, CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [MacOSFact]
    public async Task PathRelationUsesVolumeCaseAndFileIdentity()
    {
        string root = CreateTemporaryRoot();
        string mixedCase = Path.Combine(root, "MixedCase");
        string unicodeComposed = Path.Combine(root, "caf\u00e9");
        Directory.CreateDirectory(mixedCase);
        Directory.CreateDirectory(unicodeComposed);
        MacOSStoragePlatformService service = new();

        try
        {
            StoragePathInspection inspection = await service.InspectPathAsync(
                root,
                probeWriteCapabilities: false,
                CancellationToken.None);
            StoragePathRelation caseRelation = service.GetPathRelation(
                mixedCase,
                Path.Combine(root, "mixedcase"));
            StoragePathRelation unicodeRelation = service.GetPathRelation(
                unicodeComposed,
                Path.Combine(root, "cafe\u0301"));

            Assert.Equal(
                inspection.IsCaseSensitive ? StoragePathRelation.Unrelated : StoragePathRelation.Same,
                caseRelation);
            Assert.Equal(StoragePathRelation.Same, unicodeRelation);
            Assert.Equal(StoragePathRelation.Ancestor, service.GetPathRelation(root, mixedCase));
            Assert.Equal(StoragePathRelation.Descendant, service.GetPathRelation(mixedCase, root));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [MacOSFact]
    public async Task ProcessIdentityAndIdentityCheckedStopUsePidStartPathAndUid()
    {
        MacOSStoragePlatformService service = new();
        StorageProcessIdentity current = service.GetCurrentProcessIdentity();

        Assert.Equal(Environment.ProcessId, current.ProcessId);
        Assert.True(current.StartTimeUtcTicks > DateTimeOffset.UnixEpoch.UtcTicks);
        Assert.Equal(
            StoragePathRelation.Same,
            service.GetPathRelation(Environment.ProcessPath!, current.ExecutablePath));
        Assert.True(uint.TryParse(
            current.UserIdentity,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _));

        StorageProcessIdentity child = await service.StartProcessAsync(
            "/bin/sleep",
            ["30"],
            CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.StopProcessAsync(
                    child with { StartTimeUtcTicks = child.StartTimeUtcTicks + 1 },
                    CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await service.StopProcessAsync(
                    child with { UserIdentity = "4294967295" },
                    CancellationToken.None));

            await service.StopProcessAsync(child, CancellationToken.None);
            await service.WaitForProcessExitAsync(child, CancellationToken.None);
        }
        finally
        {
            try
            {
                await service.StopProcessAsync(child, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [MacOSFact]
    public void OpenDirectoryRejectsMissingPathAndUsesStructuredWorkspaceAdapter()
    {
        string root = CreateTemporaryRoot();
        string? opened = null;
        MacOSStoragePlatformService service = new(
            _ => new MacOSVolumeMetadata(
                "00000000-0000-0000-0000-000000000001",
                "apfs",
                "APFS",
                Internal: true,
                Removable: false,
                Ejectable: false,
                Writable: true,
                CaseSensitive: false),
            path =>
            {
                opened = path;
                return true;
            });

        try
        {
            Assert.False(service.OpenDirectory(Path.Combine(root, "missing")));
            Assert.True(service.OpenDirectory(root));
            Assert.Equal(Path.GetFullPath(root), opened);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [MacOSFact]
    public void NativeAbiAndConservativeVolumeClassificationRemainStable()
    {
        Assert.Equal(144, Marshal.SizeOf<MacOSFileStatus>());
        Assert.Equal(2168, Marshal.SizeOf<MacOSFileSystemStatus>());
        Assert.Equal(136, Marshal.SizeOf<MacOSProcessBsdInfo>());

        MacOSVolumeMetadata fixedMetadata = new(
            "00000000-0000-0000-0000-000000000001",
            "apfs",
            "APFS",
            Internal: true,
            Removable: false,
            Ejectable: false,
            Writable: true,
            CaseSensitive: false);
        Assert.Equal(
            StorageVolumeKind.Fixed,
            MacOSStoragePlatformService.ClassifyVolume("apfs", 0x1000, 0, fixedMetadata));
        Assert.Equal(
            StorageVolumeKind.Network,
            MacOSStoragePlatformService.ClassifyVolume("smbfs", 0, 0, fixedMetadata));
        Assert.Equal(
            StorageVolumeKind.Removable,
            MacOSStoragePlatformService.ClassifyVolume(
                "apfs",
                0x1200,
                0,
                fixedMetadata with { Internal = false, Removable = true }));
        Assert.Equal(
            StorageVolumeKind.Unknown,
            MacOSStoragePlatformService.ClassifyVolume(
                "apfs",
                0x1001,
                0,
                fixedMetadata with { Writable = false }));
        Assert.Equal(
            StorageVolumeKind.Unknown,
            MacOSStoragePlatformService.ClassifyVolume("fusefs", 0x1000, 0, fixedMetadata));
        Assert.Equal(
            StorageVolumeKind.Fixed,
            MacOSStoragePlatformService.ClassifyVolume(
                "apfs",
                0x1000,
                1,
                fixedMetadata with { Internal = false, Ejectable = true }));
    }

    private static string CreateTemporaryRoot()
    {
        string temporary = Path.GetTempPath();
        if (temporary.StartsWith("/var/", StringComparison.Ordinal) ||
            temporary.Equals("/var", StringComparison.Ordinal))
        {
            temporary = "/private" + temporary;
        }
        else if (temporary.StartsWith("/tmp/", StringComparison.Ordinal) ||
                 temporary.Equals("/tmp", StringComparison.Ordinal))
        {
            temporary = "/private" + temporary;
        }

        string root = Path.Combine(temporary, $"SnapBoard.Storage.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

[CollectionDefinition(
    MacOSStoragePlatformServiceTests.CollectionName,
    DisableParallelization = true)]
public sealed class MacOSStorageNativeGroup;
