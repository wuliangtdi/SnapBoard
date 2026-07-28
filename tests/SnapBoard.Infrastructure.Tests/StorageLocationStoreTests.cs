using SnapBoard.Infrastructure.Storage;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Infrastructure.Tests;

public sealed class StorageLocationStoreTests
{
    [Fact]
    public async Task FirstInitializationCreatesVersionedLocatorAndInstanceMarker()
    {
        await using StorageLocationTestContext context = await StorageLocationTestContext.CreateAsync();

        ResolvedStorageLocation first = await context.Store.ResolveOrCreateAsync(
            startupMigrationId: null,
            CancellationToken.None);
        ResolvedStorageLocation second = await context.Store.ResolveOrCreateAsync(
            startupMigrationId: null,
            CancellationToken.None);

        Assert.Equal(context.Bootstrap.DefaultDataRoot, first.Paths.RootDirectory);
        Assert.Equal(first.Location.StorageInstanceId, second.Location.StorageInstanceId);
        Assert.Equal(StorageDocumentVersions.Location, first.Location.FormatVersion);
        Assert.True(File.Exists(context.Bootstrap.LocationPath));
        Assert.True(File.Exists(Path.Combine(first.Paths.RootDirectory, "storage-instance.json")));
        Assert.Equal(64, first.Location.Integrity.Length);
    }

    [Fact]
    public async Task ExistingLegacyDatabaseRemainsTheInitialDataRoot()
    {
        string root = CreateTemporaryRoot();
        StorageBootstrapPaths bootstrap = StorageBootstrapPaths.Create(root);
        await File.WriteAllBytesAsync(
            Path.Combine(bootstrap.LegacyDataRoot, "snapboard.db"),
            [0x53]);
        await using StorageLocationStore store = new(bootstrap, new FakeStoragePlatformService());

        ResolvedStorageLocation resolved = await store.ResolveOrCreateAsync(
            startupMigrationId: null,
            CancellationToken.None);

        Assert.Equal(bootstrap.LegacyDataRoot, resolved.Paths.RootDirectory);
        TryDeleteDirectory(root);
    }

    [Fact]
    public async Task CorruptPrimaryLocatorRecoversOnlyFromValidBackup()
    {
        await using StorageLocationTestContext context = await StorageLocationTestContext.CreateAsync();
        ResolvedStorageLocation resolved = await context.Store.ResolveOrCreateAsync(
            startupMigrationId: null,
            CancellationToken.None);
        await context.Store.WriteLocationAsync(
            resolved.Location with
            {
                LastMigrationId = "migration-backup-test",
                Integrity = string.Empty,
            },
            CancellationToken.None);
        await File.WriteAllTextAsync(context.Bootstrap.LocationPath, "{\"tampered\":true}");

        ResolvedStorageLocation recovered = await context.Store.ResolveOrCreateAsync(
            startupMigrationId: null,
            CancellationToken.None);

        Assert.Null(recovered.Location.LastMigrationId);
        Assert.Equal(resolved.Location.StorageInstanceId, recovered.Location.StorageInstanceId);
    }

    [Fact]
    public async Task MissingConfiguredCustomRootDoesNotCreateBlankStorage()
    {
        await using StorageLocationTestContext context = await StorageLocationTestContext.CreateAsync();
        ResolvedStorageLocation resolved = await context.Store.ResolveOrCreateAsync(
            startupMigrationId: null,
            CancellationToken.None);
        string missing = Path.Combine(context.Root, "detached-volume", "custom-data");
        await context.Store.WriteLocationAsync(
            resolved.Location with
            {
                CurrentDataRoot = missing,
                VolumeIdentity = "volume-detached",
                Integrity = string.Empty,
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<StorageLocationUnavailableException>(async () =>
            await context.Store.ResolveOrCreateAsync(
                startupMigrationId: null,
                CancellationToken.None));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task UnknownLocatorFieldsAreRejectedWithoutAValidBackup()
    {
        await using StorageLocationTestContext context = await StorageLocationTestContext.CreateAsync();
        _ = await context.Store.ResolveOrCreateAsync(
            startupMigrationId: null,
            CancellationToken.None);
        string json = await File.ReadAllTextAsync(context.Bootstrap.LocationPath);
        string tampered = json[..^1] + ",\"unknownField\":1}";
        await File.WriteAllTextAsync(context.Bootstrap.LocationPath, tampered);

        await Assert.ThrowsAsync<StorageMetadataException>(async () =>
            await context.Store.ResolveOrCreateAsync(
                startupMigrationId: null,
                CancellationToken.None));
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"SnapBoard.Storage.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StorageLocationTestContext : IAsyncDisposable
    {
        private StorageLocationTestContext(
            string root,
            StorageBootstrapPaths bootstrap,
            StorageLocationStore store)
        {
            Root = root;
            Bootstrap = bootstrap;
            Store = store;
        }

        public string Root { get; }

        public StorageBootstrapPaths Bootstrap { get; }

        public StorageLocationStore Store { get; }

        public static ValueTask<StorageLocationTestContext> CreateAsync()
        {
            string root = CreateTemporaryRoot();
            StorageBootstrapPaths bootstrap = StorageBootstrapPaths.Create(root);
            StorageLocationStore store = new(bootstrap, new FakeStoragePlatformService());
            return ValueTask.FromResult(new StorageLocationTestContext(root, bootstrap, store));
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            TryDeleteDirectory(Root);
        }
    }

    private sealed class FakeStoragePlatformService : IStoragePlatformService
    {
        public ValueTask<StoragePathInspection> InspectPathAsync(
            string path,
            bool probeWriteCapabilities,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new StoragePathInspection(
                Path.GetFullPath(path),
                "volume-fixed-test",
                StorageVolumeKind.Fixed,
                "testfs",
                16L * 1024 * 1024 * 1024,
                ContainsReparsePoint: false,
                IsPrivateToCurrentUser: true,
                SupportsWriteThroughAndAtomicRename: true));
        }

        public ValueTask EnsurePrivateDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(path);
            return ValueTask.CompletedTask;
        }

        public StorageProcessIdentity GetCurrentProcessIdentity() => throw new NotSupportedException();

        public ValueTask WaitForProcessExitAsync(
            StorageProcessIdentity process,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<StorageProcessIdentity> StartProcessAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask StopProcessAsync(
            StorageProcessIdentity process,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool OpenDirectory(string path) => false;
    }
}
