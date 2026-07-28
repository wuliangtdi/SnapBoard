using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Infrastructure.Storage;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Infrastructure.Tests;

public sealed class StorageMigrationExecutorTests
{
    [Fact]
    public async Task MigratesVerifiedDatabaseAndBlobThenPreservesSourceBackup()
    {
        await using MigrationTestContext context = await MigrationTestContext.CreateAsync();
        context.Platform.Started = async process =>
        {
            await context.LocationStore.WriteStartupAcknowledgementAsync(
                new StorageStartupAcknowledgementDocument(
                    StorageDocumentVersions.StartupAcknowledgement,
                    context.Manifest.MigrationId,
                    context.Manifest.StorageInstanceId,
                    process,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    string.Empty),
                CancellationToken.None);
        };
        StorageMigrationExecutor executor = new(context.Platform);

        StorageMigrationExecutionResult result = await executor.ExecuteAsync(
            context.ManifestPath,
            CancellationToken.None);

        Assert.Equal(StorageMigrationExecutionStatus.Completed, result.Status);
        Assert.NotNull(result.BackupDirectory);
        Assert.True(Directory.Exists(result.BackupDirectory));
        Assert.False(Directory.Exists(context.Manifest.SourceDataRoot));
        Assert.True(Directory.Exists(context.Manifest.TargetDataRoot));
        StorageLocationDocument location = Assert.IsType<StorageLocationDocument>(
            await context.LocationStore.ReadLocationAsync(CancellationToken.None));
        StorageMigrationStateDocument state = Assert.IsType<StorageMigrationStateDocument>(
            await context.LocationStore.ReadMigrationStateAsync(CancellationToken.None));
        Assert.Equal(context.Manifest.TargetDataRoot, location.CurrentDataRoot);
        Assert.Equal(result.BackupDirectory, location.RollbackDataRoot);
        Assert.Equal(StorageMigrationPhase.Completed, state.Phase);
        Assert.False(File.Exists(context.ManifestPath));

        await using HistoryStoreTestContext migrated = await HistoryStoreTestContext.CreateAsync(
            context.Manifest.TargetDataRoot);
        ClipboardHistoryContent? content = await migrated.Store.GetContentAsync(
            context.ItemId,
            CancellationToken.None);
        Assert.NotNull(content);
        Assert.Equal(70 * 1024, content.Html.Length);
    }

    [Fact]
    public async Task MissingStartupAcknowledgementRollsBackAndQuarantinesTarget()
    {
        await using MigrationTestContext context = await MigrationTestContext.CreateAsync();
        StorageMigrationExecutor executor = new(
            context.Platform,
            startupAcknowledgementTimeout: TimeSpan.FromMilliseconds(40),
            startupAcknowledgementPollInterval: TimeSpan.FromMilliseconds(5));

        StorageMigrationExecutionResult result = await executor.ExecuteAsync(
            context.ManifestPath,
            CancellationToken.None);

        Assert.Equal(StorageMigrationExecutionStatus.RolledBack, result.Status);
        Assert.Equal("startup-timeout", result.ErrorCode);
        Assert.Equal(2, context.Platform.StartCount);
        Assert.Equal(1, context.Platform.StopCount);
        StorageLocationDocument location = Assert.IsType<StorageLocationDocument>(
            await context.LocationStore.ReadLocationAsync(CancellationToken.None));
        StorageMigrationStateDocument state = Assert.IsType<StorageMigrationStateDocument>(
            await context.LocationStore.ReadMigrationStateAsync(CancellationToken.None));
        Assert.Equal(context.Manifest.SourceDataRoot, location.CurrentDataRoot);
        Assert.Equal(StorageMigrationPhase.RolledBack, state.Phase);
        Assert.True(Directory.Exists(context.Manifest.SourceDataRoot));
        Assert.False(Directory.Exists(context.Manifest.TargetDataRoot));
        Assert.True(Directory.Exists(
            $"{context.Manifest.TargetDataRoot}.failed-{context.Manifest.MigrationId}"));
    }

    [Fact]
    public async Task TargetThatBecomesNonEmptyAfterMainExitIsPreservedAndMigrationRollsBack()
    {
        await using MigrationTestContext context = await MigrationTestContext.CreateAsync();
        string existingFile = Path.Combine(
            context.Manifest.TargetDataRoot,
            "created-after-confirmation.txt");
        await File.WriteAllTextAsync(existingFile, "keep");
        StorageMigrationExecutor executor = new(context.Platform);

        StorageMigrationExecutionResult result = await executor.ExecuteAsync(
            context.ManifestPath,
            CancellationToken.None);

        Assert.Equal(StorageMigrationExecutionStatus.RolledBack, result.Status);
        Assert.Equal("verification-failed", result.ErrorCode);
        Assert.True(File.Exists(existingFile));
        Assert.True(Directory.Exists(context.Manifest.SourceDataRoot));
        Assert.Equal(1, context.Platform.StartCount);
        StorageLocationDocument location = Assert.IsType<StorageLocationDocument>(
            await context.LocationStore.ReadLocationAsync(CancellationToken.None));
        Assert.Equal(context.Manifest.SourceDataRoot, location.CurrentDataRoot);
    }

    private sealed class MigrationTestContext : IAsyncDisposable
    {
        private MigrationTestContext(
            string root,
            StorageLocationStore locationStore,
            FakeMigrationPlatformService platform,
            StorageMigrationManifest manifest,
            string manifestPath,
            ClipboardItemId itemId)
        {
            Root = root;
            LocationStore = locationStore;
            Platform = platform;
            Manifest = manifest;
            ManifestPath = manifestPath;
            ItemId = itemId;
        }

        public string Root { get; }

        public StorageLocationStore LocationStore { get; }

        public FakeMigrationPlatformService Platform { get; }

        public StorageMigrationManifest Manifest { get; }

        public string ManifestPath { get; }

        public ClipboardItemId ItemId { get; }

        public static async ValueTask<MigrationTestContext> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"SnapBoard.Migration.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string mainExecutable = CreateExecutable(root, "SnapBoard.Desktop.exe");
            string migratorExecutable = CreateExecutable(root, "SnapBoard.StorageMigrator.exe");
            StorageProcessIdentity mainProcess = new(
                1001,
                DateTimeOffset.UtcNow.UtcTicks,
                mainExecutable,
                "test-user");
            StorageProcessIdentity migratorProcess = new(
                1002,
                DateTimeOffset.UtcNow.UtcTicks + 1,
                migratorExecutable,
                "test-user");
            FakeMigrationPlatformService platform = new(migratorProcess);
            StorageBootstrapPaths bootstrap = StorageBootstrapPaths.Create(root);
            StorageLocationStore locationStore = new(bootstrap, platform);
            ResolvedStorageLocation active = await locationStore.ResolveOrCreateAsync(
                startupMigrationId: null,
                CancellationToken.None);

            ClipboardItemId itemId;
            await using (HistoryStoreTestContext history = await HistoryStoreTestContext.CreateAsync(
                active.Paths.RootDirectory,
                deleteOnDispose: false))
            {
                ClipboardHistorySaveResult saved = await history.Store.SaveAsync(
                    CreateLargeHtmlItem(),
                    CancellationToken.None);
                itemId = saved.ItemId;
                await history.Store.PrepareForMigrationAsync(CancellationToken.None);
            }

            string target = Path.Combine(root, "migrated-data");
            Directory.CreateDirectory(target);
            string migrationId = $"m-{Guid.NewGuid():N}";
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            StorageMigrationManifest manifest = new(
                StorageDocumentVersions.MigrationManifest,
                migrationId,
                bootstrap.BootstrapDirectory,
                active.Paths.RootDirectory,
                target,
                active.Location.StorageInstanceId,
                active.Location.VolumeIdentity,
                "volume-fixed-test",
                RequiredBytes: 256L * 1024 * 1024,
                mainProcess,
                mainExecutable,
                migratorExecutable,
                now,
                string.Empty);
            string manifestPath = bootstrap.GetManifestPath(migrationId);
            await locationStore.WriteManifestAsync(
                manifestPath,
                manifest,
                CancellationToken.None);
            await locationStore.WriteMigrationStateAsync(
                new StorageMigrationStateDocument(
                    StorageDocumentVersions.MigrationState,
                    migrationId,
                    StorageMigrationPhase.Requested,
                    manifest.SourceDataRoot,
                    manifest.TargetDataRoot,
                    manifest.StorageInstanceId,
                    now,
                    now,
                    LocatorSwitched: false,
                    StartupAcknowledged: false,
                    ErrorCode: null,
                    Integrity: string.Empty),
                CancellationToken.None);
            return new MigrationTestContext(
                root,
                locationStore,
                platform,
                manifest,
                manifestPath,
                itemId);
        }

        public async ValueTask DisposeAsync()
        {
            await LocationStore.DisposeAsync();
            SqliteConnection.ClearAllPools();
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

        private static string CreateExecutable(string root, string name)
        {
            string path = Path.Combine(root, name);
            File.WriteAllBytes(path, [0x4d, 0x5a]);
            return Path.GetFullPath(path);
        }
    }

    private sealed class FakeMigrationPlatformService(StorageProcessIdentity currentProcess) :
        IStoragePlatformService
    {
        public Func<StorageProcessIdentity, ValueTask>? Started { get; set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

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

        public StorageProcessIdentity GetCurrentProcessIdentity() => currentProcess;

        public ValueTask WaitForProcessExitAsync(
            StorageProcessIdentity process,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<StorageProcessIdentity> StartProcessAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            StorageProcessIdentity process = new(
                2000 + StartCount,
                DateTimeOffset.UtcNow.UtcTicks + StartCount,
                Path.GetFullPath(executablePath),
                currentProcess.UserIdentity);
            if (Started is not null && StartCount == 1)
            {
                await Started(process);
            }

            return process;
        }

        public ValueTask StopProcessAsync(
            StorageProcessIdentity process,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return ValueTask.CompletedTask;
        }

        public bool OpenDirectory(string path) => false;
    }

    private static ClipboardCapturedItem CreateLargeHtmlItem()
    {
        byte[] html = Enumerable.Repeat((byte)'h', 70 * 1024).ToArray();
        ClipboardItemId id = ClipboardItemId.New();
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = DateTimeOffset.UtcNow,
            SourceProcessName = "migration-test",
            SourceAccessStatus = 0,
            ContentHash = new ClipboardContentHash(
                Convert.ToHexStringLower(SHA256.HashData(html))),
            PrimaryKind = ClipboardContentKind.Html,
            DisplayCategory = ClipboardHistoryDisplayCategory.Text,
            PreviewText = "large html",
            SearchableText = "large html migration",
            Representations =
            [
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Html,
                    "text/html",
                    null,
                    html),
            ],
            Formats = [new ClipboardCapturedFormat("html", "HTML", true)],
            TotalSizeBytes = html.Length,
        };
    }
}
