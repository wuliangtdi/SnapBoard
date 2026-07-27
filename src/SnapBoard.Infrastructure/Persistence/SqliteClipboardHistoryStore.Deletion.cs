using System.Globalization;
using Microsoft.Data.Sqlite;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Infrastructure.Persistence;

public sealed partial class SqliteClipboardHistoryStore
{
    private async ValueTask<bool> SoftDeleteCoreAsync(
        SqliteConnection connection,
        ClipboardItemId itemId,
        CancellationToken cancellationToken) =>
        await DeleteItemsCoreAsync(connection, [itemId.ToString()], cancellationToken)
            .ConfigureAwait(false) > 0;

    private async ValueTask<int> ClearCoreAsync(
        SqliteConnection connection,
        bool includePinned,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = includePinned
            ? "SELECT id FROM clipboard_items WHERE is_deleted = 0;"
            : "SELECT id FROM clipboard_items WHERE is_deleted = 0 AND is_pinned = 0;";
        IReadOnlyList<string> identifiers = await ReadIdentifiersAsync(command, cancellationToken)
            .ConfigureAwait(false);
        return await DeleteItemsCoreAsync(connection, identifiers, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<int> ApplyRetentionCoreAsync(
        SqliteConnection connection,
        ClipboardRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH ranked AS (
                SELECT id,
                       captured_at_utc,
                       ROW_NUMBER() OVER (
                           ORDER BY captured_at_utc DESC, id DESC
                       ) AS item_rank,
                       SUM(total_size_bytes) OVER (
                           ORDER BY captured_at_utc DESC, id DESC
                           ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
                       ) AS retained_bytes
                FROM clipboard_items
                WHERE is_deleted = 0 AND is_pinned = 0
            )
            SELECT id
            FROM ranked
            WHERE captured_at_utc < @cutoff
               OR item_rank > @maximumItemCount
               OR retained_bytes > @maximumStorageBytes
            ORDER BY captured_at_utc ASC, id ASC;
            """;
        command.Parameters.AddWithValue(
            "@cutoff",
            (now - policy.MaximumAge).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@maximumItemCount", policy.MaximumItemCount);
        command.Parameters.AddWithValue("@maximumStorageBytes", policy.MaximumStorageBytes);
        IReadOnlyList<string> identifiers = await ReadIdentifiersAsync(command, cancellationToken)
            .ConfigureAwait(false);
        return await DeleteItemsCoreAsync(connection, identifiers, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<int> DeleteItemsCoreAsync(
        SqliteConnection connection,
        IReadOnlyCollection<string> identifiers,
        CancellationToken cancellationToken)
    {
        string[] distinctIdentifiers = identifiers
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctIdentifiers.Length == 0)
        {
            return 0;
        }

        List<string> filesToDelete = [];
        int deletedCount;
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            // 临时表避免动态拼接 IN 列表，也让大批量清理保持参数化。
            await ExecuteAsync(
                    connection,
                    transaction,
                    "CREATE TEMP TABLE IF NOT EXISTS pending_clipboard_deletes(id TEXT PRIMARY KEY) WITHOUT ROWID;",
                    cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM pending_clipboard_deletes;",
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (string identifier in distinctIdentifiers)
            {
                await using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT OR IGNORE INTO pending_clipboard_deletes(id) VALUES (@id);";
                insert.Parameters.AddWithValue("@id", identifier);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM pending_clipboard_deletes
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM clipboard_items i
                        WHERE i.id = pending_clipboard_deletes.id AND i.is_deleted = 0
                    );
                    """,
                    cancellationToken)
                .ConfigureAwait(false);

            await using (SqliteCommand count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM pending_clipboard_deletes;";
                deletedCount = Convert.ToInt32(
                    await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            if (deletedCount == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            IReadOnlyList<BlobReferenceRemoval> blobReferences =
                await ReadBlobReferenceRemovalsAsync(
                        connection,
                        transaction,
                        cancellationToken)
                    .ConfigureAwait(false);

            await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM clipboard_items_fts WHERE item_id IN (SELECT id FROM pending_clipboard_deletes);",
                    cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM clipboard_item_tags WHERE item_id IN (SELECT id FROM pending_clipboard_deletes);",
                    cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM clipboard_representations WHERE item_id IN (SELECT id FROM pending_clipboard_deletes);",
                    cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM clipboard_files WHERE item_id IN (SELECT id FROM pending_clipboard_deletes);",
                    cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM clipboard_formats WHERE item_id IN (SELECT id FROM pending_clipboard_deletes);",
                    cancellationToken)
                .ConfigureAwait(false);

            await using (SqliteCommand tombstone = connection.CreateCommand())
            {
                tombstone.Transaction = transaction;
                tombstone.CommandText = """
                    UPDATE clipboard_items
                    SET source_process_id = NULL,
                        source_process_name = NULL,
                        source_executable_path = NULL,
                        source_application_user_model_id = NULL,
                        source_package_family_name = NULL,
                        source_attribution_kind = 0,
                        content_hash = '',
                        preview_text = '',
                        searchable_text = '',
                        is_pinned = 0,
                        is_deleted = 1,
                        deleted_at_utc = @deletedAt,
                        updated_at_utc = @deletedAt,
                        total_size_bytes = 0,
                        thumbnail_blob_hash = NULL
                    WHERE id IN (SELECT id FROM pending_clipboard_deletes);
                    """;
                tombstone.Parameters.AddWithValue(
                    "@deletedAt",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                int affected = await tombstone.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (affected != deletedCount)
                {
                    throw new InvalidDataException("Clipboard tombstone count was inconsistent.");
                }
            }

            foreach (BlobReferenceRemoval reference in blobReferences)
            {
                if (reference.ExistingReferenceCount < reference.RemovedReferenceCount)
                {
                    throw new InvalidDataException("Clipboard blob reference count was inconsistent.");
                }

                await using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                if (reference.ExistingReferenceCount == reference.RemovedReferenceCount)
                {
                    update.CommandText = """
                        DELETE FROM content_blobs
                        WHERE hash = @hash AND ref_count = @removedReferenceCount;
                        """;
                    filesToDelete.Add(reference.RelativePath);
                }
                else
                {
                    update.CommandText = """
                        UPDATE content_blobs
                        SET ref_count = ref_count - @removedReferenceCount
                        WHERE hash = @hash AND ref_count = @existingReferenceCount;
                        """;
                    update.Parameters.AddWithValue(
                        "@existingReferenceCount",
                        reference.ExistingReferenceCount);
                }

                update.Parameters.AddWithValue("@hash", reference.Hash);
                update.Parameters.AddWithValue(
                    "@removedReferenceCount",
                    reference.RemovedReferenceCount);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException("Clipboard blob reference update was inconsistent.");
                }
            }

            await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM clipboard_tags
                    WHERE NOT EXISTS (
                        SELECT 1 FROM clipboard_item_tags it WHERE it.tag_id = clipboard_tags.id
                    );
                    """,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        foreach (string relativePath in filesToDelete.Distinct(BlobPathComparer))
        {
            try
            {
                await _blobStore.DeleteAsync(relativePath).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // 数据库引用已经提交移除；后台孤儿清理会按精确相对路径重试。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deletedCount;
    }

    private static async ValueTask<IReadOnlyList<BlobReferenceRemoval>>
        ReadBlobReferenceRemovalsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH removed_references(blob_hash) AS (
                SELECT r.blob_hash
                FROM clipboard_representations r
                JOIN pending_clipboard_deletes p ON p.id = r.item_id
                WHERE r.blob_hash IS NOT NULL
                UNION ALL
                SELECT i.thumbnail_blob_hash
                FROM clipboard_items i
                JOIN pending_clipboard_deletes p ON p.id = i.id
                WHERE i.thumbnail_blob_hash IS NOT NULL
            )
            SELECT b.hash, b.relative_path, b.ref_count, COUNT(*)
            FROM removed_references r
            JOIN content_blobs b ON b.hash = r.blob_hash
            GROUP BY b.hash, b.relative_path, b.ref_count;
            """;
        List<BlobReferenceRemoval> result = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BlobReferenceRemoval(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return result;
    }

    private async ValueTask<int> CleanupOrphanedBlobBatchCoreAsync(
        SqliteConnection connection,
        IReadOnlyList<string> relativePaths,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        int deleted = 0;
        foreach (string relativePath in relativePaths.Distinct(BlobPathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM content_blobs WHERE relative_path = @relativePath
                );
                """;
            command.Parameters.AddWithValue("@relativePath", relativePath);
            int referenced = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (referenced == 0 && _blobStore.DeleteIfOlderThan(relativePath, cutoff))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private static async ValueTask<IReadOnlyList<string>> ReadIdentifiersAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        List<string> identifiers = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            identifiers.Add(reader.GetString(0));
        }

        return identifiers;
    }

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record BlobReferenceRemoval(
        string Hash,
        string RelativePath,
        long ExistingReferenceCount,
        long RemovedReferenceCount);
}
