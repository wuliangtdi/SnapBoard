using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Infrastructure.Persistence;

namespace SnapBoard.Infrastructure.Tests;

public sealed class SqliteClipboardHistoryStoreTests
{
    [Fact]
    public async Task InitializeEnablesRequiredPragmasAndIsRepeatable()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();

        ClipboardHistoryInitializationResult second = await context.Store.InitializeAsync(
            CancellationToken.None);
        Assert.False(second.RecoveredCorruptDatabase);

        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        Assert.Equal("wal", await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, await ExecuteScalarInt64Async(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5000L, await ExecuteScalarInt64Async(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await ExecuteScalarInt64Async(connection, "PRAGMA user_version;"));
        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await ExecuteScalarInt64Async(connection, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task EverySchemaVersionCanBeCreatedAndRepeatedMigrationIsIdempotent(
        int targetVersion)
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync(
            initialize: false);
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);

        Assert.Equal(
            targetVersion,
            await context.Migrator.MigrateAsync(
                connection,
                CancellationToken.None,
                targetVersion));
        Assert.Equal(
            targetVersion,
            await context.Migrator.MigrateAsync(
                connection,
                CancellationToken.None,
                targetVersion));
        Assert.Equal(
            targetVersion,
            await ExecuteScalarInt64Async(connection, "PRAGMA user_version;"));
        Assert.Equal(
            targetVersion,
            await ExecuteScalarInt64Async(connection, "SELECT COUNT(*) FROM schema_migrations;"));

        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await context.Migrator.MigrateAsync(connection, CancellationToken.None));
        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await context.Migrator.MigrateAsync(connection, CancellationToken.None));
        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await ExecuteScalarInt64Async(connection, "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    public async Task VersionOneDatabaseMigratesToCurrentVersion()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync(
            initialize: false);
        await using (SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(
                1,
                await context.Migrator.MigrateAsync(
                    connection,
                    CancellationToken.None,
                    targetVersion: 1));
        }

        ClipboardHistoryInitializationResult result = await context.Store.InitializeAsync(
            CancellationToken.None);
        Assert.False(result.RecoveredCorruptDatabase);

        await using SqliteConnection migrated = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await ExecuteScalarInt64Async(migrated, "PRAGMA user_version;"));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                migrated,
                "SELECT COUNT(*) FROM pragma_table_info('clipboard_items') WHERE name = 'capture_count';"));
    }

    [Fact]
    public async Task VersionTwoMigrationRebuildsFtsWithAlignedRowIds()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync(
            initialize: false);
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            2,
            await context.Migrator.MigrateAsync(
                connection,
                CancellationToken.None,
                targetVersion: 2));

        await ExecuteNonQueryAsync(
            connection,
            "INSERT INTO clipboard_items_fts(item_id, searchable_text) VALUES ('detached', 'detached row');");
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO clipboard_items(
                id, sequence_number, primary_kind, display_category,
                captured_at_utc, updated_at_utc, source_access_status,
                content_hash, preview_text, searchable_text, total_size_bytes)
            VALUES (
                '11111111-1111-1111-1111-111111111111', '1', 1, 1,
                1, 1, 0, 'hash', '中文迁移', '中文迁移', 12);
            """);
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO clipboard_items_fts(item_id, searchable_text)
            VALUES ('11111111-1111-1111-1111-111111111111', '中文迁移');
            """);

        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await context.Migrator.MigrateAsync(connection, CancellationToken.None));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                connection,
                """
                SELECT COUNT(*)
                FROM clipboard_items i
                JOIN clipboard_items_fts f ON f.rowid = i.search_order_key
                WHERE f.item_id = i.id;
                """));
        Assert.Equal(
            0L,
            await ExecuteScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM clipboard_items_fts WHERE item_id = 'detached';"));
    }

    [Fact]
    public async Task VersionFourMigrationAddsSourceApplicationIdentityColumns()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync(
            initialize: false);
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            4,
            await context.Migrator.MigrateAsync(
                connection,
                CancellationToken.None,
                targetVersion: 4));
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT INTO clipboard_items(
                id, sequence_number, primary_kind, display_category,
                captured_at_utc, updated_at_utc, source_access_status,
                content_hash, preview_text, searchable_text, total_size_bytes)
            VALUES (
                '44444444-4444-4444-4444-444444444444', '4', 1, 1,
                4, 4, 0, 'v4-hash', 'v4 source', 'v4 source', 9);
            """);

        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await context.Migrator.MigrateAsync(connection, CancellationToken.None));
        Assert.Equal(
            SnapBoardDatabaseMigrator.CurrentVersion,
            await context.Migrator.MigrateAsync(connection, CancellationToken.None));
        Assert.Equal(
            3L,
            await ExecuteScalarInt64Async(
                connection,
                """
                SELECT COUNT(*)
                FROM pragma_table_info('clipboard_items')
                WHERE name IN (
                    'source_application_user_model_id',
                    'source_package_family_name',
                    'source_attribution_kind');
                """));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                connection,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'index' AND name = 'ix_clipboard_items_source_identity';
                """));
        await using SqliteCommand source = connection.CreateCommand();
        source.CommandText = """
            SELECT
                source_application_user_model_id,
                source_package_family_name,
                source_attribution_kind
            FROM clipboard_items
            WHERE id = '44444444-4444-4444-4444-444444444444';
            """;
        await using SqliteDataReader reader = await source.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(await reader.IsDBNullAsync(0));
        Assert.True(await reader.IsDBNullAsync(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Fact]
    public async Task FailedMigrationRollsBackSchemaAndVersion()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync(
            initialize: false);
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        await context.Migrator.MigrateAsync(
            connection,
            CancellationToken.None,
            targetVersion: 2);
        await ExecuteNonQueryAsync(
            connection,
            "ALTER TABLE clipboard_items RENAME COLUMN searchable_text TO missing_searchable_text;");

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Migrator.MigrateAsync(connection, CancellationToken.None));

        Assert.Equal(2L, await ExecuteScalarInt64Async(connection, "PRAGMA user_version;"));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'clipboard_items_fts';"));
        Assert.Equal(2L, await ExecuteScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM schema_migrations;"));
    }

    [Fact]
    public async Task RestartPreservesHistoryTagsPinsAndSettings()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"SnapBoard.Infrastructure.Tests-{Guid.NewGuid():N}");
        ClipboardCapturedItem item = CreateTextItem(
            "restart persistence 中文",
            sourceExecutablePath: @"C:\Program Files\Example\example.exe",
            sourceApplicationUserModelId: "Example.App_123!App",
            sourcePackageFamilyName: "Example.App_123",
            sourceAttributionKind: 1);
        ClipboardCapturedItem deletedItem = CreateTextItem("restart deleted state");
        DateTimeOffset usedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        await using (HistoryStoreTestContext first = await HistoryStoreTestContext.CreateAsync(
            root,
            deleteOnDispose: false))
        {
            await first.Store.SaveAsync(item, CancellationToken.None);
            Assert.True(await first.Store.SetPinnedAsync(item.Id, true, CancellationToken.None));
            Assert.True(await first.Store.SetTagsAsync(
                item.Id,
                ["work", "中文"],
                CancellationToken.None));
            Assert.True(await first.Store.RecordUseAsync(
                item.Id,
                usedAt,
                CancellationToken.None));
            await first.Store.SaveAsync(deletedItem, CancellationToken.None);
            Assert.True(await first.Store.SoftDeleteAsync(
                deletedItem.Id,
                CancellationToken.None));
            await first.Store.SetSettingAsync("history.page-size", "75", CancellationToken.None);
        }

        await using HistoryStoreTestContext second = await HistoryStoreTestContext.CreateAsync(root);
        ClipboardHistoryPage page = await second.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None);
        ClipboardHistoryItemSummary restored = Assert.Single(page.Items);
        Assert.Equal(item.Id, restored.Id);
        Assert.Equal(item.SourceExecutablePath, restored.SourceExecutablePath);
        Assert.Equal(
            item.SourceApplicationUserModelId,
            restored.SourceApplicationUserModelId);
        Assert.Equal(item.SourcePackageFamilyName, restored.SourcePackageFamilyName);
        Assert.Equal(item.SourceAttributionKind, restored.SourceAttributionKind);
        Assert.True(restored.IsPinned);
        Assert.Equal(["work", "中文"], restored.Tags);
        Assert.Equal(1, restored.UseCount);
        Assert.Equal(usedAt, restored.LastUsedAt);
        Assert.Equal(
            "75",
            await second.Store.GetSettingAsync("history.page-size", CancellationToken.None));
        Assert.Equal(
            "restart persistence 中文",
            (await second.Store.GetContentAsync(item.Id, CancellationToken.None))?.Text);
        await using SqliteConnection connection = await second.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            3L,
            await ExecuteScalarInt64Async(
                connection,
                """
                SELECT is_deleted + (deleted_at_utc IS NOT NULL) * 2
                FROM clipboard_items
                WHERE id = @id;
                """,
                deletedItem.Id.ToString()));
    }

    [Fact]
    public async Task UnknownMacOSSourceKeepsWindowsIdentityNullAcrossRestart()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"SnapBoard.Infrastructure.Tests-{Guid.NewGuid():N}");
        ClipboardCapturedItem item = CreateTextItem(
            "macOS unknown source persistence",
            sourceProcessName: null);
        await using (HistoryStoreTestContext first = await HistoryStoreTestContext.CreateAsync(
            root,
            deleteOnDispose: false))
        {
            await first.Store.SaveAsync(item, CancellationToken.None);
        }

        await using HistoryStoreTestContext second = await HistoryStoreTestContext.CreateAsync(root);
        ClipboardHistoryItemSummary restored = Assert.Single((await second.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None)).Items);
        Assert.Equal("未知来源", restored.SourceApplication);
        Assert.Null(restored.SourceExecutablePath);
        Assert.Null(restored.SourceApplicationUserModelId);
        Assert.Null(restored.SourcePackageFamilyName);
        Assert.Equal(0, restored.SourceAttributionKind);

        await using SqliteConnection connection = await second.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                source_application_user_model_id,
                source_package_family_name,
                source_access_status,
                source_attribution_kind
            FROM clipboard_items
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", item.Id.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(await reader.IsDBNullAsync(0));
        Assert.True(await reader.IsDBNullAsync(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
    }

    [Fact]
    public async Task FailedTransactionRemovesNewlyStagedBlob()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        byte[] largePayload = CreateLargePayload();
        ClipboardCapturedItem invalid = CreateItem(
            "rollback",
            [
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Html,
                    "text/html",
                    null,
                    largePayload),
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Html,
                    "text/html",
                    null,
                    largePayload),
            ]);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Store.SaveAsync(invalid, CancellationToken.None));

        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(0L, await ExecuteScalarInt64Async(connection, "SELECT COUNT(*) FROM clipboard_items;"));
        Assert.Equal(0L, await ExecuteScalarInt64Async(connection, "SELECT COUNT(*) FROM content_blobs;"));
        Assert.Empty(Directory.Exists(context.Paths.BlobDirectory)
            ? Directory.EnumerateFiles(context.Paths.BlobDirectory, "*.blob", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task SharedBlobIsDeletedOnlyAfterLastReference()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        byte[] sharedPayload = CreateLargePayload();
        ClipboardCapturedItem first = CreateLargeHtmlItem("first", sharedPayload);
        ClipboardCapturedItem second = CreateLargeHtmlItem("second", sharedPayload);

        await context.Store.SaveAsync(first, CancellationToken.None);
        await context.Store.SaveAsync(second, CancellationToken.None);

        (string relativePath, long referenceCount) = await ReadSingleBlobAsync(context);
        string fullPath = Path.Combine(context.Paths.BlobDirectory, relativePath);
        Assert.Equal(2L, referenceCount);
        Assert.True(File.Exists(fullPath));

        Assert.True(await context.Store.SoftDeleteAsync(first.Id, CancellationToken.None));
        Assert.True(File.Exists(fullPath));
        Assert.Equal(1L, (await ReadSingleBlobAsync(context)).ReferenceCount);

        Assert.True(await context.Store.SoftDeleteAsync(second.Id, CancellationToken.None));
        Assert.False(File.Exists(fullPath));
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(0L, await ExecuteScalarInt64Async(connection, "SELECT COUNT(*) FROM content_blobs;"));
        Assert.Equal(2L, await ExecuteScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM clipboard_items WHERE is_deleted = 1;"));
    }

    [Fact]
    public async Task AdjacentDuplicateMergesUnlessPreviousItemIsPinned()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        ClipboardCapturedItem first = CreateTextItem("adjacent duplicate");
        ClipboardCapturedItem second = CreateTextItem("adjacent duplicate");

        ClipboardHistorySaveResult firstResult = await context.Store.SaveAsync(
            first,
            CancellationToken.None);
        ClipboardHistorySaveResult secondResult = await context.Store.SaveAsync(
            second,
            CancellationToken.None);

        Assert.False(firstResult.WasMerged);
        Assert.True(secondResult.WasMerged);
        Assert.Equal(first.Id, secondResult.ItemId);
        await using (SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(1L, await ExecuteScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM clipboard_items WHERE is_deleted = 0;"));
            Assert.Equal(2L, await ExecuteScalarInt64Async(
                connection,
                "SELECT capture_count FROM clipboard_items WHERE id = @id;",
                first.Id.ToString()));
        }

        Assert.True(await context.Store.SetPinnedAsync(first.Id, true, CancellationToken.None));
        ClipboardHistorySaveResult thirdResult = await context.Store.SaveAsync(
            CreateTextItem("adjacent duplicate"),
            CancellationToken.None);
        Assert.False(thirdResult.WasMerged);
    }

    [Fact]
    public async Task SQLiteFtsSearchHonorsCancellationDuringRealQuery()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-1);
        const int itemCount = 30_000;
        const int batchSize = 2_000;
        for (int offset = 0; offset < itemCount; offset += batchSize)
        {
            ClipboardCapturedItem[] batch = Enumerable.Range(offset, batchSize)
                .Select(index => CreateTextItem(
                    $"cancellation needle {index:D5} {new string('x', 256)}",
                    start.AddMilliseconds(index)))
                .ToArray();
            await context.Store.BulkImportAsync(batch, CancellationToken.None);
        }

        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await context.Store.SearchAsync(
                new ClipboardHistoryQuery
                {
                    SearchText = "cancellation needle",
                    IncludeSearchResultCount = true,
                    PageSize = 50,
                },
                cancellation.Token));
    }

    [Fact]
    public async Task OrphanCleanupUsesExactRelativePathAndPreservesReferencedBlob()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        ClipboardCapturedItem item = CreateLargeHtmlItem("live", CreateLargePayload());
        await context.Store.SaveAsync(item, CancellationToken.None);
        (string relativePath, _) = await ReadSingleBlobAsync(context);
        string livePath = Path.Combine(context.Paths.BlobDirectory, relativePath);
        DateTime old = DateTime.UtcNow - TimeSpan.FromDays(2);
        File.SetLastWriteTimeUtc(livePath, old);

        string fileName = Path.GetFileName(livePath);
        string orphanDirectory = Path.Combine(context.Paths.BlobDirectory, "ff");
        Directory.CreateDirectory(orphanDirectory);
        string orphanPath = Path.Combine(orphanDirectory, fileName);
        File.Copy(livePath, orphanPath);
        File.SetLastWriteTimeUtc(orphanPath, old);

        string temporaryDirectory = Path.Combine(context.Paths.BlobDirectory, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        string expiredTemporaryPath = Path.Combine(temporaryDirectory, "expired.tmp");
        string recentTemporaryPath = Path.Combine(temporaryDirectory, "recent.tmp");
        await File.WriteAllBytesAsync(expiredTemporaryPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(recentTemporaryPath, [4, 5, 6]);
        File.SetLastWriteTimeUtc(expiredTemporaryPath, old);

        Assert.Equal(1, await context.Store.CleanupOrphanedBlobsAsync(CancellationToken.None));
        Assert.True(File.Exists(livePath));
        Assert.False(File.Exists(orphanPath));
        Assert.False(File.Exists(expiredTemporaryPath));
        Assert.True(File.Exists(recentTemporaryPath));
    }

    [Fact]
    public async Task InitializationDoesNotSynchronouslyDeleteOldOrphanBlob()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync(
            initialize: false);
        string orphanDirectory = Path.Combine(context.Paths.BlobDirectory, "ab");
        Directory.CreateDirectory(orphanDirectory);
        string orphanPath = Path.Combine(orphanDirectory, $"{new string('a', 64)}.blob");
        await File.WriteAllBytesAsync(orphanPath, [1, 2, 3]);
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow - TimeSpan.FromDays(2));

        await context.Store.InitializeAsync(CancellationToken.None);

        Assert.True(File.Exists(orphanPath));
    }

    [Fact]
    public async Task SearchSupportsFtsSpecialCharactersAndStablePagination()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        DateTimeOffset start = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        ClipboardCapturedItem[] items =
        [
            CreateTextItem("中文剪贴板检索", start),
            CreateTextItem("persistent clipboard history", start.AddMinutes(1)),
            CreateTextItem("public static void Main() { Console.WriteLine(\"token\"); }", start.AddMinutes(2)),
            CreateTextItem("pagination fourth", start.AddMinutes(3)),
            CreateTextItem("pagination fifth", start.AddMinutes(4)),
        ];
        foreach (ClipboardCapturedItem item in items)
        {
            await context.Store.SaveAsync(item, CancellationToken.None);
        }

        ClipboardHistoryPage code = await context.Store.SearchAsync(
            new ClipboardHistoryQuery { SearchText = "Console.WriteLine(\"token\")", PageSize = 10 },
            CancellationToken.None);
        Assert.Equal(items[2].Id, Assert.Single(code.Items).Id);
        Assert.Equal(
            items[0].Id,
            Assert.Single((await context.Store.SearchAsync(
                new ClipboardHistoryQuery { SearchText = "中文剪贴板", PageSize = 10 },
                CancellationToken.None)).Items).Id);

        ClipboardHistoryPage firstPage = await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 2 },
            CancellationToken.None);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextCursor);
        ClipboardHistoryPage secondPage = await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 2, Cursor = firstPage.NextCursor },
            CancellationToken.None);
        Assert.Equal(2, secondPage.Items.Count);
        Assert.Empty(firstPage.Items.Select(item => item.Id).Intersect(
            secondPage.Items.Select(item => item.Id)));
        Assert.Equal(5L, firstPage.TotalCount);
    }

    [Fact]
    public async Task FtsPaginationCrossesPinnedBoundaryWithoutDuplicates()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        ClipboardCapturedItem[] items = Enumerable.Range(0, 6)
            .Select(index => CreateTextItem(
                $"shared-search stable item {index}",
                index < 2 ? capturedAt : capturedAt.AddSeconds(index)))
            .ToArray();
        foreach (ClipboardCapturedItem item in items)
        {
            await context.Store.SaveAsync(item, CancellationToken.None);
        }

        Assert.True(await context.Store.SetPinnedAsync(items[1].Id, true, CancellationToken.None));
        List<ClipboardHistoryItemSummary> all = [];
        ClipboardHistoryCursor? cursor = null;
        do
        {
            ClipboardHistoryPage page = await context.Store.SearchAsync(
                new ClipboardHistoryQuery
                {
                    SearchText = "shared-search",
                    Cursor = cursor,
                    PageSize = 2,
                },
                CancellationToken.None);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(items.Length, all.Count);
        Assert.Equal(items.Length, all.Select(item => item.Id).Distinct().Count());
        Assert.Equal(items[1].Id, all[0].Id);
        Assert.True(all[0].IsPinned);
        Assert.All(all.Skip(1), item => Assert.False(item.IsPinned));
    }

    [Fact]
    public async Task SearchCombinesTypeSourceTimeTagAndPinnedFilters()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-2);
        ClipboardCapturedItem matching = CreateTextItem(
            "public static filtered-search() { }",
            start.AddHours(1),
            "filter-app");
        ClipboardCapturedItem wrongSource = CreateTextItem(
            "public static filtered-search() { } other",
            start.AddHours(1),
            "other-app");
        await context.Store.SaveAsync(matching, CancellationToken.None);
        await context.Store.SaveAsync(wrongSource, CancellationToken.None);
        await context.Store.SetTagsAsync(matching.Id, ["work"], CancellationToken.None);
        await context.Store.SetTagsAsync(wrongSource.Id, ["work"], CancellationToken.None);
        await context.Store.SetPinnedAsync(matching.Id, true, CancellationToken.None);

        ClipboardHistoryPage page = await context.Store.SearchAsync(
            new ClipboardHistoryQuery
            {
                SearchText = "filtered-search",
                DisplayCategory = ClipboardHistoryDisplayCategory.Code,
                SourceApplication = "FILTER-APP",
                CapturedAfter = start.AddMinutes(30),
                CapturedBefore = start.AddMinutes(90),
                Tags = ["WORK"],
                IsPinned = true,
                PageSize = 10,
            },
            CancellationToken.None);

        Assert.Equal(matching.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task PngAndTiffOriginalsUseAtomicBlobStagingAndThumbnailsLoadOnDemand()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        byte[] tiff = CreateDecodableTiff();
        (ClipboardCapturedItem Item, byte[] Original, ClipboardStoredBitmapEncoding Encoding)[] images =
        [
            (
                CreateImageItem(
                    "png image",
                    "image/png",
                    png,
                    ClipboardStoredBitmapEncoding.PortableNetworkGraphics),
                png,
                ClipboardStoredBitmapEncoding.PortableNetworkGraphics),
            (
                CreateImageItem(
                    "tiff image",
                    "image/tiff",
                    tiff,
                    ClipboardStoredBitmapEncoding.TaggedImageFileFormat),
                tiff,
                ClipboardStoredBitmapEncoding.TaggedImageFileFormat),
        ];

        foreach ((ClipboardCapturedItem item, _, _) in images)
        {
            await context.Store.SaveAsync(item, CancellationToken.None);
        }

        ClipboardHistoryPage page = await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None);
        Assert.Equal(images.Length, page.Items.Count);
        Assert.All(page.Items, summary => Assert.True(summary.HasThumbnail));
        foreach ((ClipboardCapturedItem item, byte[] original, ClipboardStoredBitmapEncoding encoding)
            in images)
        {
            ReadOnlyMemory<byte> thumbnail = await context.Store.GetThumbnailAsync(
                item.Id,
                CancellationToken.None);
            Assert.True(thumbnail.Span.StartsWith(
                new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }));

            ClipboardHistoryContent? content = await context.Store.GetContentAsync(
                item.Id,
                CancellationToken.None);
            Assert.NotNull(content?.Bitmap);
            Assert.Equal(encoding, content.Bitmap.Encoding);
            Assert.Equal(original, content.Bitmap.Data.ToArray());

            string originalRelativePath = await ReadBlobPathAsync(
                context,
                """
                SELECT b.relative_path
                FROM clipboard_representations r
                JOIN content_blobs b ON b.hash = r.blob_hash
                WHERE r.item_id = @id AND r.kind = @kind;
                """,
                item.Id,
                includeKindParameter: true);
            Assert.Equal(
                original,
                await File.ReadAllBytesAsync(Path.Combine(
                    context.Paths.BlobDirectory,
                    originalRelativePath)));

            string thumbnailRelativePath = await ReadBlobPathAsync(
                context,
                """
                SELECT b.relative_path
                FROM clipboard_items i
                JOIN content_blobs b ON b.hash = i.thumbnail_blob_hash
                WHERE i.id = @id;
                """,
                item.Id);
            Assert.Equal(
                thumbnail.ToArray(),
                await File.ReadAllBytesAsync(Path.Combine(
                    context.Paths.BlobDirectory,
                    thumbnailRelativePath)));
        }

        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(context.Paths.BlobDirectory, ".tmp"),
            "*.tmp"));
    }

    [Fact]
    public async Task MalformedTiffOriginalIsPreservedWithoutThumbnail()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        byte[] malformed = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0];
        ClipboardCapturedItem item = CreateImageItem(
            "malformed tiff image",
            "image/tiff",
            malformed,
            ClipboardStoredBitmapEncoding.TaggedImageFileFormat);

        await context.Store.SaveAsync(item, CancellationToken.None);

        ClipboardHistoryItemSummary summary = Assert.Single((await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None)).Items);
        Assert.False(summary.HasThumbnail);
        ClipboardHistoryContent? content = await context.Store.GetContentAsync(
            item.Id,
            CancellationToken.None);
        Assert.NotNull(content?.Bitmap);
        Assert.Equal(malformed, content.Bitmap.Data.ToArray());
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(context.Paths.BlobDirectory, ".tmp"),
            "*.tmp"));
    }

    [Fact]
    public async Task RetentionAndClearPreservePinnedItemsUntilExplicitlyIncluded()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        DateTimeOffset start = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        ClipboardCapturedItem[] items = Enumerable.Range(0, 5)
            .Select(index => CreateTextItem($"retention-{index}", start.AddMinutes(index)))
            .ToArray();
        foreach (ClipboardCapturedItem item in items)
        {
            await context.Store.SaveAsync(item, CancellationToken.None);
        }

        Assert.True(await context.Store.SetPinnedAsync(items[0].Id, true, CancellationToken.None));
        int removed = await context.Store.ApplyRetentionAsync(
            new ClipboardRetentionPolicy(
                maximumItemCount: 2,
                maximumAge: TimeSpan.FromDays(365),
                maximumStorageBytes: long.MaxValue),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.Equal(2, removed);

        ClipboardHistoryPage afterRetention = await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None);
        Assert.Equal(3, afterRetention.Items.Count);
        Assert.Contains(afterRetention.Items, item => item.Id == items[0].Id && item.IsPinned);

        Assert.Equal(2, await context.Store.ClearAsync(false, CancellationToken.None));
        ClipboardHistoryItemSummary pinned = Assert.Single((await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None)).Items);
        Assert.Equal(items[0].Id, pinned.Id);
        Assert.Equal(1, await context.Store.ClearAsync(true, CancellationToken.None));
        Assert.Empty((await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None)).Items);
    }

    private static ClipboardCapturedItem CreateTextItem(
        string text,
        DateTimeOffset? capturedAt = null,
        string? sourceProcessName = "test-source",
        string? sourceExecutablePath = null,
        string? sourceApplicationUserModelId = null,
        string? sourcePackageFamilyName = null,
        int sourceAttributionKind = 0) => CreateItem(
        text,
        [
            new ClipboardCapturedRepresentation(
                ClipboardContentKind.Text,
                "text/plain; charset=utf-8",
                text,
                default),
        ],
        capturedAt,
        sourceProcessName,
        sourceExecutablePath,
        sourceApplicationUserModelId,
        sourcePackageFamilyName,
        sourceAttributionKind);

    private static ClipboardCapturedItem CreateLargeHtmlItem(string suffix, byte[] sharedPayload) =>
        CreateItem(
            $"large-{suffix}",
            [
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Text,
                    "text/plain; charset=utf-8",
                    suffix,
                    default),
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Html,
                    "text/html",
                    null,
                    sharedPayload),
            ]);

    private static ClipboardCapturedItem CreateItem(
        string seed,
        IReadOnlyList<ClipboardCapturedRepresentation> representations,
        DateTimeOffset? capturedAt = null,
        string? sourceProcessName = "test-source",
        string? sourceExecutablePath = null,
        string? sourceApplicationUserModelId = null,
        string? sourcePackageFamilyName = null,
        int sourceAttributionKind = 0)
    {
        ClipboardItemId id = ClipboardItemId.New();
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
            SourceProcessName = sourceProcessName,
            SourceExecutablePath = sourceExecutablePath,
            SourceApplicationUserModelId = sourceApplicationUserModelId,
            SourcePackageFamilyName = sourcePackageFamilyName,
            SourceAccessStatus = 0,
            SourceAttributionKind = sourceAttributionKind,
            ContentHash = Hash(seed),
            PrimaryKind = representations.Any(representation =>
                representation.Kind == ClipboardContentKind.Image)
                ? ClipboardContentKind.Image
                : representations.Any(representation =>
                    representation.Kind == ClipboardContentKind.Html)
                    ? ClipboardContentKind.Html
                    : ClipboardContentKind.Text,
            DisplayCategory = representations.Any(representation =>
                representation.Kind == ClipboardContentKind.Image)
                ? ClipboardHistoryDisplayCategory.Image
                : seed.Contains("public ", StringComparison.Ordinal)
                    ? ClipboardHistoryDisplayCategory.Code
                    : ClipboardHistoryDisplayCategory.Text,
            PreviewText = seed,
            SearchableText = seed,
            Representations = representations,
            Formats = [new ClipboardCapturedFormat("test", "Test", true)],
            TotalSizeBytes = representations.Sum(representation => representation.SizeBytes),
        };
    }

    private static ClipboardContentHash Hash(string value) => new(
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))));

    private static byte[] CreateLargePayload() => Enumerable
        .Repeat((byte)'x', 70 * 1024)
        .ToArray();

    private static ClipboardCapturedItem CreateImageItem(
        string seed,
        string mediaType,
        byte[] data,
        ClipboardStoredBitmapEncoding encoding) => CreateItem(
        seed,
        [
            new ClipboardCapturedRepresentation(
                ClipboardContentKind.Image,
                mediaType,
                null,
                data,
                encoding,
                1,
                1,
                8),
        ]);

    private static byte[] CreateDecodableTiff()
    {
        const ushort entryCount = 13;
        const int directoryOffset = 8;
        const int bitsPerSampleOffset = directoryOffset + 2 + (entryCount * 12) + 4;
        const int xResolutionOffset = bitsPerSampleOffset + 6;
        const int yResolutionOffset = xResolutionOffset + 8;
        const int pixelOffset = yResolutionOffset + 8;
        byte[] data = new byte[pixelOffset + 3];
        data[0] = (byte)'I';
        data[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), directoryOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(directoryOffset), entryCount);
        int entryOffset = directoryOffset + 2;
        WriteTiffEntry(data.AsSpan(entryOffset, 12), 256, 3, 1, 1);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 257, 3, 1, 1);
        WriteTiffEntry(
            data.AsSpan(entryOffset += 12, 12),
            258,
            3,
            3,
            bitsPerSampleOffset);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 259, 3, 1, 1);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 262, 3, 1, 2);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 273, 4, 1, pixelOffset);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 277, 3, 1, 3);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 278, 4, 1, 1);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 279, 4, 1, 3);
        WriteTiffEntry(
            data.AsSpan(entryOffset += 12, 12),
            282,
            5,
            1,
            xResolutionOffset);
        WriteTiffEntry(
            data.AsSpan(entryOffset += 12, 12),
            283,
            5,
            1,
            yResolutionOffset);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 284, 3, 1, 1);
        WriteTiffEntry(data.AsSpan(entryOffset += 12, 12), 296, 3, 1, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(bitsPerSampleOffset), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(bitsPerSampleOffset + 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(bitsPerSampleOffset + 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(xResolutionOffset), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(xResolutionOffset + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(yResolutionOffset), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(yResolutionOffset + 4), 1);
        data[pixelOffset] = 0x40;
        data[pixelOffset + 1] = 0x80;
        data[pixelOffset + 2] = 0xC0;
        return data;
    }

    private static void WriteTiffEntry(
        Span<byte> entry,
        ushort tag,
        ushort type,
        uint count,
        uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], value);
    }

    private static async Task<string> ReadBlobPathAsync(
        HistoryStoreTestContext context,
        string commandText,
        ClipboardItemId itemId,
        bool includeKindParameter = false)
    {
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("@id", itemId.ToString());
        if (includeKindParameter)
        {
            command.Parameters.AddWithValue("@kind", (int)ClipboardContentKind.Image);
        }

        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<(string RelativePath, long ReferenceCount)> ReadSingleBlobAsync(
        HistoryStoreTestContext context)
    {
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path, ref_count FROM content_blobs;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        (string RelativePath, long ReferenceCount) result = (reader.GetString(0), reader.GetInt64(1));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static async Task<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        string commandText)
        => await ExecuteScalarInt64Async(connection, commandText, null);

    private static async Task<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        string commandText,
        string? identifier)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        if (identifier is not null)
        {
            command.Parameters.AddWithValue("@id", identifier);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }
}
