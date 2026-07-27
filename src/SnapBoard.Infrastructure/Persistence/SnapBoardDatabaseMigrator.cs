using Microsoft.Data.Sqlite;

namespace SnapBoard.Infrastructure.Persistence;

public sealed class SnapBoardDatabaseMigrator
{
    private readonly DatabaseMigration[] _migrations =
    [
        new(1, "history-core-v1",
        [
            """
            CREATE TABLE schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc INTEGER NOT NULL
            );
            """,
            """
            CREATE TABLE content_blobs (
                hash TEXT NOT NULL PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                media_type TEXT NOT NULL,
                size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
                ref_count INTEGER NOT NULL CHECK (ref_count > 0),
                created_at_utc INTEGER NOT NULL
            );
            """,
            """
            CREATE TABLE clipboard_items (
                id TEXT NOT NULL PRIMARY KEY,
                sequence_number TEXT NOT NULL,
                primary_kind INTEGER NOT NULL,
                display_category INTEGER NOT NULL,
                captured_at_utc INTEGER NOT NULL,
                updated_at_utc INTEGER NOT NULL,
                source_process_id INTEGER NULL,
                source_process_name TEXT NULL,
                source_executable_path TEXT NULL,
                source_access_status INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                preview_text TEXT NOT NULL,
                searchable_text TEXT NOT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0, 1)),
                use_count INTEGER NOT NULL DEFAULT 0 CHECK (use_count >= 0),
                last_used_at_utc INTEGER NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1)),
                deleted_at_utc INTEGER NULL,
                total_size_bytes INTEGER NOT NULL CHECK (total_size_bytes >= 0),
                thumbnail_blob_hash TEXT NULL REFERENCES content_blobs(hash)
            );
            """,
            """
            CREATE TABLE clipboard_representations (
                item_id TEXT NOT NULL REFERENCES clipboard_items(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL,
                media_type TEXT NOT NULL,
                inline_text TEXT NULL,
                inline_data BLOB NULL,
                blob_hash TEXT NULL REFERENCES content_blobs(hash),
                size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
                bitmap_encoding INTEGER NULL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                bits_per_pixel INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (item_id, kind)
            );
            """,
            """
            CREATE TABLE clipboard_files (
                item_id TEXT NOT NULL REFERENCES clipboard_items(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                path TEXT NOT NULL,
                PRIMARY KEY (item_id, ordinal)
            );
            """,
            """
            CREATE TABLE clipboard_formats (
                item_id TEXT NOT NULL REFERENCES clipboard_items(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                identifier TEXT NOT NULL,
                name TEXT NOT NULL,
                is_available INTEGER NOT NULL CHECK (is_available IN (0, 1)),
                PRIMARY KEY (item_id, ordinal)
            );
            """,
            """
            CREATE TABLE clipboard_tags (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                normalized_name TEXT NOT NULL UNIQUE,
                created_at_utc INTEGER NOT NULL
            );
            """,
            """
            CREATE TABLE clipboard_item_tags (
                item_id TEXT NOT NULL REFERENCES clipboard_items(id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES clipboard_tags(id) ON DELETE CASCADE,
                PRIMARY KEY (item_id, tag_id)
            );
            """,
            """
            CREATE VIRTUAL TABLE clipboard_items_fts USING fts5(
                item_id UNINDEXED,
                searchable_text,
                tokenize = 'trigram'
            );
            """,
            "CREATE INDEX ix_clipboard_items_active_order ON clipboard_items(is_deleted, is_pinned DESC, captured_at_utc DESC, id DESC);",
            "CREATE INDEX ix_clipboard_items_hash_order ON clipboard_items(is_deleted, captured_at_utc DESC, id DESC, content_hash);",
            "CREATE INDEX ix_clipboard_items_source ON clipboard_items(is_deleted, source_process_name, captured_at_utc DESC);",
            "CREATE INDEX ix_clipboard_items_kind ON clipboard_items(is_deleted, primary_kind, display_category, captured_at_utc DESC);",
        ]),
        new(2, "settings-rules-and-capture-count-v2",
        [
            "ALTER TABLE clipboard_items ADD COLUMN capture_count INTEGER NOT NULL DEFAULT 1 CHECK (capture_count > 0);",
            """
            CREATE TABLE settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1,
                updated_at_utc INTEGER NOT NULL
            );
            """,
            """
            CREATE TABLE application_rules (
                normalized_application TEXT NOT NULL PRIMARY KEY,
                mode INTEGER NOT NULL,
                maximum_payload_bytes INTEGER NULL,
                updated_at_utc INTEGER NOT NULL
            );
            """,
            "CREATE INDEX ix_clipboard_item_tags_tag ON clipboard_item_tags(tag_id, item_id);",
            "CREATE INDEX ix_content_blobs_ref_count ON content_blobs(ref_count, created_at_utc);",
        ]),
        new(3, "align-fts-rowid-v3",
        [
            "DROP TABLE clipboard_items_fts;",
            """
            CREATE VIRTUAL TABLE clipboard_items_fts USING fts5(
                item_id UNINDEXED,
                searchable_text,
                tokenize = 'trigram'
            );
            """,
            """
            INSERT INTO clipboard_items_fts(rowid, item_id, searchable_text)
            SELECT rowid, id, searchable_text
            FROM clipboard_items
            WHERE is_deleted = 0;
            """,
        ]),
        new(4, "ordered-fts-pagination-v4",
        [
            "ALTER TABLE clipboard_items ADD COLUMN search_order_key INTEGER NULL;",
            """
            WITH ranked AS (
                SELECT
                    rowid AS item_rowid,
                    captured_at_utc * 1000000 +
                        ROW_NUMBER() OVER (
                            PARTITION BY captured_at_utc
                            ORDER BY id
                        ) - 1 AS generated_key
                FROM clipboard_items
            )
            UPDATE clipboard_items
            SET search_order_key = (
                SELECT generated_key
                FROM ranked
                WHERE ranked.item_rowid = clipboard_items.rowid
            );
            """,
            "CREATE UNIQUE INDEX ux_clipboard_items_search_order ON clipboard_items(search_order_key);",
            "CREATE INDEX ix_clipboard_items_capture_tie ON clipboard_items(captured_at_utc, search_order_key);",
            "CREATE INDEX ix_clipboard_items_search_phase ON clipboard_items(is_deleted, is_pinned, search_order_key);",
            "DROP TABLE clipboard_items_fts;",
            """
            CREATE VIRTUAL TABLE clipboard_items_fts USING fts5(
                item_id UNINDEXED,
                searchable_text,
                tokenize = 'trigram'
            );
            """,
            """
            INSERT INTO clipboard_items_fts(rowid, item_id, searchable_text)
            SELECT search_order_key, id, searchable_text
            FROM clipboard_items
            WHERE is_deleted = 0;
            """,
        ]),
        new(5, "source-application-identity-v5",
        [
            "ALTER TABLE clipboard_items ADD COLUMN source_application_user_model_id TEXT NULL;",
            "ALTER TABLE clipboard_items ADD COLUMN source_package_family_name TEXT NULL;",
            "ALTER TABLE clipboard_items ADD COLUMN source_attribution_kind INTEGER NOT NULL DEFAULT 0;",
            "CREATE INDEX ix_clipboard_items_source_identity ON clipboard_items(is_deleted, source_application_user_model_id, captured_at_utc DESC);",
        ]),
    ];

    public const int CurrentVersion = 5;

    public async ValueTask<int> MigrateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        int targetVersion = CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (targetVersion is < 0 or > CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        }

        int currentVersion = await GetUserVersionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (currentVersion > targetVersion)
        {
            throw new InvalidOperationException(
                $"Database version {currentVersion} is newer than supported version {targetVersion}.");
        }

        foreach (DatabaseMigration migration in _migrations.Where(migration =>
            migration.Version > currentVersion && migration.Version <= targetVersion))
        {
            await ApplyMigrationAsync(connection, migration, cancellationToken)
                .ConfigureAwait(false);
            currentVersion = migration.Version;
        }

        return currentVersion;
    }

    private static async ValueTask ApplyMigrationAsync(
        SqliteConnection connection,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            foreach (string sql in migration.Commands)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO schema_migrations(version, name, applied_at_utc)
                    VALUES (@version, @name, @appliedAt);
                    """;
                record.Parameters.AddWithValue("@version", migration.Version);
                record.Parameters.AddWithValue("@name", migration.Name);
                record.Parameters.AddWithValue(
                    "@appliedAt",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = $"PRAGMA user_version = {migration.Version};";
                await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<int> GetUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record DatabaseMigration(
        int Version,
        string Name,
        IReadOnlyList<string> Commands);
}
