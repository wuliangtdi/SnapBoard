using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Infrastructure.Storage;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class MacOSStorageStartupContextTests
{
    [Fact]
    public void DefaultBootstrapRootUsesCurrentUsersApplicationSupport()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "SnapBoard");

        Assert.Equal(expected, StorageBootstrapPaths.CreateDefault().ApplicationDataDirectory);
    }

    [Fact]
    public void ExistingLegacyDataRootWinsOverNewEmptyDefaultRoot()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string applicationData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            $"SnapBoard-Startup-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(applicationData);
        File.WriteAllText(Path.Combine(applicationData, "snapboard.db"), "legacy-history");
        Directory.CreateDirectory(Path.Combine(applicationData, "blobs"));
        Directory.CreateDirectory(Path.Combine(applicationData, "recovery"));

        try
        {
            using DesktopStorageStartupContext context = MacOSStorageStartupContext.Create(
                applicationData,
                migrationId: null);

            Assert.Equal(applicationData, context.ActiveLocation.Paths.RootDirectory);
            Assert.False(Directory.Exists(Path.Combine(applicationData, "data")));
            Assert.Equal(
                Path.Combine(applicationData, "bootstrap", "storage-location.json"),
                context.BootstrapPaths.LocationPath);
        }
        finally
        {
            Directory.Delete(applicationData, recursive: true);
        }
    }

    [Theory]
    [InlineData("/tmp/snapboard-publish", "/tmp/snapboard-publish/SnapBoard.StorageMigrator")]
    [InlineData(
        "/Applications/SnapBoard.app/Contents/MacOS",
        "/Applications/SnapBoard.app/Contents/MacOS/SnapBoard.StorageMigrator")]
    public void MigratorPathSupportsBarePublishAndAppBundle(
        string baseDirectory,
        string expected)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal(
            expected,
            MacOSDesktopLifecycleCoordinator.ResolveStorageMigratorExecutablePath(baseDirectory));
    }
}
