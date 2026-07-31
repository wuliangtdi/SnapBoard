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
            "CREATE INDEX ix_clipboard_items_active_order ON clipboard_items(is_deleted, captured_at_utc DESC, id DESC);",
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
            "CREATE INDEX ix_clipboard_items_search_order ON clipboard_items(is_deleted, search_order_key);",
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
        new(6, "encrypted-sync-state-v6",
        [
            """
            CREATE TABLE sync_spaces (
                space_id TEXT NOT NULL PRIMARY KEY,
                key_version INTEGER NOT NULL CHECK (key_version > 0),
                is_enabled INTEGER NOT NULL DEFAULT 0 CHECK (is_enabled IN (0, 1)),
                tombstone_retention_days INTEGER NOT NULL DEFAULT 30
                    CHECK (tombstone_retention_days BETWEEN 7 AND 3650),
                created_at_utc INTEGER NOT NULL,
                updated_at_utc INTEGER NOT NULL,
                CHECK (length(space_id) = 32)
            );
            """,
            """
            CREATE TABLE sync_devices (
                space_id TEXT NOT NULL REFERENCES sync_spaces(space_id) ON DELETE CASCADE,
                device_id TEXT NOT NULL,
                is_local INTEGER NOT NULL CHECK (is_local IN (0, 1)),
                next_sequence INTEGER NOT NULL DEFAULT 1 CHECK (next_sequence > 0),
                logical_clock INTEGER NOT NULL DEFAULT 0 CHECK (logical_clock >= 0),
                revoked_at_utc INTEGER NULL,
                created_at_utc INTEGER NOT NULL,
                updated_at_utc INTEGER NOT NULL,
                PRIMARY KEY (space_id, device_id),
                CHECK (length(device_id) = 32)
            );
            """,
            """
            CREATE TABLE sync_outbox (
                event_id TEXT NOT NULL PRIMARY KEY,
                space_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                event_json BLOB NOT NULL CHECK (
                    (state = 2 AND length(event_json) = 0) OR
                    (state IN (0, 1) AND length(event_json) BETWEEN 1 AND 8388608)
                ),
                state INTEGER NOT NULL DEFAULT 0 CHECK (state IN (0, 1, 2)),
                retry_count INTEGER NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
                next_attempt_at_utc INTEGER NOT NULL,
                last_error_category TEXT NULL,
                remote_etag TEXT NULL,
                created_at_utc INTEGER NOT NULL,
                uploaded_at_utc INTEGER NULL,
                UNIQUE (space_id, device_id, sequence),
                FOREIGN KEY (space_id, device_id)
                    REFERENCES sync_devices(space_id, device_id) ON DELETE CASCADE,
                CHECK (length(event_id) = 32)
            );
            """,
            """
            CREATE TABLE sync_inbox (
                space_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                event_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                payload_hash TEXT NOT NULL,
                applied_at_utc INTEGER NOT NULL,
                PRIMARY KEY (space_id, device_id, event_id),
                UNIQUE (space_id, device_id, sequence),
                FOREIGN KEY (space_id, device_id)
                    REFERENCES sync_devices(space_id, device_id) ON DELETE CASCADE,
                CHECK (length(event_id) = 32),
                CHECK (length(payload_hash) = 64)
            );
            """,
            """
            CREATE TABLE sync_checkpoints (
                space_id TEXT NOT NULL,
                remote_device_id TEXT NOT NULL,
                applied_sequence INTEGER NOT NULL DEFAULT 0 CHECK (applied_sequence >= 0),
                applied_event_id TEXT NULL,
                remote_etag TEXT NULL,
                updated_at_utc INTEGER NOT NULL,
                PRIMARY KEY (space_id, remote_device_id),
                FOREIGN KEY (space_id, remote_device_id)
                    REFERENCES sync_devices(space_id, device_id) ON DELETE CASCADE,
                CHECK (applied_event_id IS NULL OR length(applied_event_id) = 32)
            );
            """,
            """
            CREATE TABLE sync_remote_blobs (
                space_id TEXT NOT NULL REFERENCES sync_spaces(space_id) ON DELETE CASCADE,
                blob_hash TEXT NOT NULL,
                keyed_blob_id TEXT NOT NULL,
                state INTEGER NOT NULL DEFAULT 0 CHECK (state IN (0, 1, 2)),
                retry_count INTEGER NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
                next_attempt_at_utc INTEGER NOT NULL,
                last_error_category TEXT NULL,
                remote_etag TEXT NULL,
                updated_at_utc INTEGER NOT NULL,
                PRIMARY KEY (space_id, blob_hash),
                UNIQUE (space_id, keyed_blob_id),
                CHECK (length(blob_hash) = 64),
                CHECK (length(keyed_blob_id) = 64)
            );
            """,
            """
            CREATE TABLE sync_blob_staging (
                blob_hash TEXT NOT NULL PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                media_type TEXT NOT NULL,
                size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
                verified_at_utc INTEGER NOT NULL,
                CHECK (length(blob_hash) = 64)
            );
            """,
            """
            CREATE TABLE sync_item_state (
                space_id TEXT NOT NULL REFERENCES sync_spaces(space_id) ON DELETE CASCADE,
                item_id TEXT NOT NULL,
                content_logical_time INTEGER NOT NULL DEFAULT 0 CHECK (content_logical_time >= 0),
                content_device_id TEXT NULL,
                tags_logical_time INTEGER NOT NULL DEFAULT 0 CHECK (tags_logical_time >= 0),
                tags_device_id TEXT NULL,
                pin_logical_time INTEGER NOT NULL DEFAULT 0 CHECK (pin_logical_time >= 0),
                pin_device_id TEXT NULL,
                delete_logical_time INTEGER NOT NULL DEFAULT 0 CHECK (delete_logical_time >= 0),
                delete_device_id TEXT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0 CHECK (is_deleted IN (0, 1)),
                PRIMARY KEY (space_id, item_id),
                CHECK (length(item_id) = 36)
            );
            """,
            "CREATE UNIQUE INDEX ux_sync_single_enabled_space ON sync_spaces(is_enabled) WHERE is_enabled = 1;",
            "CREATE UNIQUE INDEX ux_sync_local_device ON sync_devices(space_id, is_local) WHERE is_local = 1;",
            "CREATE INDEX ix_sync_outbox_due ON sync_outbox(state, next_attempt_at_utc, sequence);",
            "CREATE INDEX ix_sync_inbox_sequence ON sync_inbox(space_id, device_id, sequence);",
            "CREATE INDEX ix_sync_remote_blobs_due ON sync_remote_blobs(state, next_attempt_at_utc);",
        ]),
        new(7, "synchronized-history-settings-v7",
        [
            """
            CREATE TABLE sync_setting_state (
                space_id TEXT NOT NULL REFERENCES sync_spaces(space_id) ON DELETE CASCADE,
                setting_key TEXT NOT NULL,
                logical_time INTEGER NOT NULL CHECK (logical_time > 0),
                device_id TEXT NOT NULL,
                PRIMARY KEY (space_id, setting_key),
                CHECK (length(setting_key) BETWEEN 1 AND 128),
                CHECK (length(device_id) = 32)
            );
            """,
        ]),
        new(8, "webdav-provider-migration-v8",
        [
            """
            CREATE TABLE sync_provider_migrations (
                plan_id TEXT NOT NULL PRIMARY KEY,
                space_id TEXT NOT NULL REFERENCES sync_spaces(space_id) ON DELETE CASCADE,
                epoch INTEGER NOT NULL CHECK (epoch > 0),
                initiator_device_id TEXT NOT NULL,
                source_remote_fingerprint TEXT NOT NULL,
                target_remote_fingerprint TEXT NOT NULL,
                state INTEGER NOT NULL CHECK (state BETWEEN 1 AND 14),
                total_objects INTEGER NOT NULL DEFAULT 0 CHECK (total_objects >= 0),
                total_bytes INTEGER NOT NULL DEFAULT 0 CHECK (total_bytes >= 0),
                completed_objects INTEGER NOT NULL DEFAULT 0 CHECK (
                    completed_objects >= 0 AND completed_objects <= total_objects
                ),
                completed_bytes INTEGER NOT NULL DEFAULT 0 CHECK (
                    completed_bytes >= 0 AND completed_bytes <= total_bytes
                ),
                inventory_sha256 TEXT NULL,
                diagnostic_code TEXT NULL,
                created_at_utc INTEGER NOT NULL,
                updated_at_utc INTEGER NOT NULL,
                UNIQUE (space_id, epoch),
                CHECK (length(plan_id) = 32),
                CHECK (length(initiator_device_id) = 32),
                CHECK (length(source_remote_fingerprint) = 64),
                CHECK (length(target_remote_fingerprint) = 64),
                CHECK (inventory_sha256 IS NULL OR length(inventory_sha256) = 64),
                CHECK (diagnostic_code IS NULL OR length(diagnostic_code) BETWEEN 1 AND 128),
                CHECK (updated_at_utc >= created_at_utc)
            );
            """,
            """
            CREATE TABLE sync_provider_migration_devices (
                plan_id TEXT NOT NULL REFERENCES sync_provider_migrations(plan_id) ON DELETE CASCADE,
                device_id TEXT NOT NULL,
                state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 5),
                highest_local_sequence INTEGER NOT NULL DEFAULT 0
                    CHECK (highest_local_sequence >= 0),
                highest_uploaded_sequence INTEGER NOT NULL DEFAULT 0 CHECK (
                    highest_uploaded_sequence >= 0 AND
                    highest_uploaded_sequence <= highest_local_sequence
                ),
                diagnostic_code TEXT NULL,
                updated_at_utc INTEGER NOT NULL,
                PRIMARY KEY (plan_id, device_id),
                CHECK (length(device_id) = 32),
                CHECK (diagnostic_code IS NULL OR length(diagnostic_code) BETWEEN 1 AND 128)
            );
            """,
            "CREATE INDEX ix_sync_provider_migrations_space_epoch ON sync_provider_migrations(space_id, epoch DESC);",
            "CREATE INDEX ix_sync_provider_migrations_state ON sync_provider_migrations(state, updated_at_utc);",
        ]),
        new(9, "source-application-icon-snapshot-v9",
        [
            "ALTER TABLE clipboard_items ADD COLUMN source_application_icon_blob_hash TEXT NULL REFERENCES content_blobs(hash);",
            "ALTER TABLE clipboard_items ADD COLUMN source_application_icon_format_version INTEGER NOT NULL DEFAULT 0 CHECK (source_application_icon_format_version >= 0);",
            "ALTER TABLE clipboard_items ADD COLUMN source_application_icon_width INTEGER NOT NULL DEFAULT 0 CHECK (source_application_icon_width >= 0);",
            "ALTER TABLE clipboard_items ADD COLUMN source_application_icon_height INTEGER NOT NULL DEFAULT 0 CHECK (source_application_icon_height >= 0);",
            "ALTER TABLE clipboard_items ADD COLUMN source_application_icon_stride INTEGER NOT NULL DEFAULT 0 CHECK (source_application_icon_stride >= 0);",
        ]),
    ];

    public const int CurrentVersion = 9;

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
