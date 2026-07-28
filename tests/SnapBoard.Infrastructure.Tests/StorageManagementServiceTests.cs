using SnapBoard.Application.Storage;
using SnapBoard.Infrastructure.Storage;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Infrastructure.Tests;

public sealed class StorageManagementServiceTests
{
    [Fact]
    public async Task ValidEmptyFixedTargetIncludesCopyAndRollbackMargin()
    {
        await using ManagementTestContext context = await ManagementTestContext.CreateAsync();
        string target = context.CreateDirectory("target");

        StorageLocationValidationResult result = await context.Service.ValidateTargetAsync(
            target,
            CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(StorageLocationValidationError.None, result.Error);
        Assert.Equal("volume-fixed-test", result.VolumeIdentity);
        Assert.True(result.RequiredBytes >= 64L * 1024 * 1024);
        Assert.Contains(Path.GetFullPath(target), context.Platform.HardenedDirectories);
    }

    [Fact]
    public async Task NestedAndNonEmptyTargetsAreRejected()
    {
        await using ManagementTestContext context = await ManagementTestContext.CreateAsync();
        string nested = Path.Combine(context.Active.Paths.RootDirectory, "nested");
        Directory.CreateDirectory(nested);
        string nonEmpty = context.CreateDirectory("non-empty");
        await File.WriteAllTextAsync(Path.Combine(nonEmpty, "existing.txt"), "occupied");

        StorageLocationValidationResult nestedResult = await context.Service.ValidateTargetAsync(
            nested,
            CancellationToken.None);
        StorageLocationValidationResult nonEmptyResult = await context.Service.ValidateTargetAsync(
            nonEmpty,
            CancellationToken.None);

        Assert.Equal(StorageLocationValidationError.NestedWithCurrent, nestedResult.Error);
        Assert.Equal(StorageLocationValidationError.ExistingStorage, nonEmptyResult.Error);
    }

    [Fact]
    public async Task PrepareMigrationPersistsOneShotManifestAndRequestedState()
    {
        await using ManagementTestContext context = await ManagementTestContext.CreateAsync();
        string target = context.CreateDirectory("target");
        string mainExecutable = context.CreateFile("install/SnapBoard.Desktop.exe");
        string migratorExecutable = context.CreateFile("install/SnapBoard.StorageMigrator.exe");
        StorageProcessIdentity process = new(
            1234,
            DateTimeOffset.UtcNow.UtcTicks,
            mainExecutable,
            "test-user");

        StorageMigrationLaunchPlan plan = await context.Service.PrepareMigrationAsync(
            target,
            process,
            mainExecutable,
            migratorExecutable,
            CancellationToken.None);

        StorageMigrationManifest manifest = Assert.IsType<StorageMigrationManifest>(
            await context.Store.ReadManifestAsync(plan.ManifestPath, CancellationToken.None));
        StorageMigrationStateDocument state = Assert.IsType<StorageMigrationStateDocument>(
            await context.Store.ReadMigrationStateAsync(CancellationToken.None));
        Assert.Equal(plan.MigrationId, manifest.MigrationId);
        Assert.Equal(context.Active.Location.StorageInstanceId, manifest.StorageInstanceId);
        Assert.Equal(StorageMigrationPhase.Requested, state.Phase);
        Assert.Equal(["--manifest", plan.ManifestPath], plan.Arguments);
        Assert.Equal(64, manifest.Integrity.Length);
        Assert.Equal(64, state.Integrity.Length);
    }

    [Fact]
    public async Task PrepareMigrationRejectsTargetThatBecameNonEmptyAfterSelection()
    {
        await using ManagementTestContext context = await ManagementTestContext.CreateAsync();
        string target = context.CreateDirectory("target");
        string mainExecutable = context.CreateFile("install/SnapBoard.Desktop.exe");
        string migratorExecutable = context.CreateFile("install/SnapBoard.StorageMigrator.exe");
        StorageProcessIdentity process = new(
            1234,
            DateTimeOffset.UtcNow.UtcTicks,
            mainExecutable,
            "test-user");
        StorageLocationValidationResult initial = await context.Service.ValidateTargetAsync(
            target,
            CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(target, "created-after-selection.txt"), "keep");

        StorageLocationValidationException exception =
            await Assert.ThrowsAsync<StorageLocationValidationException>(
                async () => await context.Service.PrepareMigrationAsync(
                    target,
                    process,
                    mainExecutable,
                    migratorExecutable,
                    CancellationToken.None));

        Assert.True(initial.IsValid);
        Assert.Equal("target-not-empty", exception.Validation.ErrorCode);
        Assert.True(File.Exists(Path.Combine(target, "created-after-selection.txt")));
        Assert.Null(await context.Store.ReadMigrationStateAsync(CancellationToken.None));
    }

    private sealed class ManagementTestContext : IAsyncDisposable
    {
        private ManagementTestContext(
            string root,
            StorageLocationStore store,
            ResolvedStorageLocation active,
            FakeStoragePlatformService platform,
            StorageManagementService service)
        {
            Root = root;
            Store = store;
            Active = active;
            Platform = platform;
            Service = service;
        }

        public string Root { get; }

        public StorageLocationStore Store { get; }

        public ResolvedStorageLocation Active { get; }

        public FakeStoragePlatformService Platform { get; }

        public StorageManagementService Service { get; }

        public static async ValueTask<ManagementTestContext> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"SnapBoard.Management.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            StorageBootstrapPaths bootstrap = StorageBootstrapPaths.Create(root);
            FakeStoragePlatformService platform = new();
            StorageLocationStore store = new(bootstrap, platform);
            ResolvedStorageLocation active = await store.ResolveOrCreateAsync(
                startupMigrationId: null,
                CancellationToken.None);
            StorageManagementService service = new(
                bootstrap,
                store,
                active,
                platform,
                installationDirectory: Path.Combine(root, "reserved-install"),
                temporaryDirectory: Path.Combine(root, "reserved-temp"),
                userHomeDirectory: Path.Combine(root, "reserved-home"),
                cloudDirectories: []);
            return new ManagementTestContext(root, store, active, platform, service);
        }

        public string CreateDirectory(string relativePath)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateFile(string relativePath)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x4d, 0x5a]);
            return Path.GetFullPath(path);
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeStoragePlatformService : IStoragePlatformService
    {
        private readonly HashSet<string> _hardenedDirectories = new(StringComparer.Ordinal);

        public IReadOnlySet<string> HardenedDirectories => _hardenedDirectories;

        public ValueTask<StoragePathInspection> InspectPathAsync(
            string path,
            bool probeWriteCapabilities,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string canonicalPath = Path.GetFullPath(path);
            return ValueTask.FromResult(new StoragePathInspection(
                canonicalPath,
                "volume-fixed-test",
                StorageVolumeKind.Fixed,
                "testfs",
                16L * 1024 * 1024 * 1024,
                ContainsReparsePoint: false,
                IsPrivateToCurrentUser: _hardenedDirectories.Contains(canonicalPath),
                SupportsWriteThroughAndAtomicRename: true));
        }

        public ValueTask EnsurePrivateDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string canonicalPath = Path.GetFullPath(path);
            Directory.CreateDirectory(canonicalPath);
            _hardenedDirectories.Add(canonicalPath);
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
