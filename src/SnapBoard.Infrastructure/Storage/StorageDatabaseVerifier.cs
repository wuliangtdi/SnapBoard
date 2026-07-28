using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SnapBoard.Infrastructure.Persistence;

namespace SnapBoard.Infrastructure.Storage;

internal sealed record StorageDatabaseSnapshot(
    int SchemaVersion,
    long ItemCount,
    long PinnedCount,
    long DeletedCount,
    long CaptureCount,
    long TagCount,
    long ItemTagCount,
    long BlobCount,
    long BlobReferenceCount,
    long RepresentationCount,
    long FileCount,
    long FormatCount,
    long SyncSpaceCount,
    long SyncDeviceCount,
    long SyncOutboxCount,
    long SyncInboxCount,
    long SyncCheckpointCount,
    long SyncRemoteBlobCount,
    long SyncBlobStagingCount,
    long SyncItemStateCount);

internal static class StorageDatabaseVerifier
{
    public static async ValueTask<StorageDatabaseSnapshot> CheckpointAndVerifyAsync(
        SnapBoardStoragePaths paths,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.DatabasePath))
        {
            throw new StorageMetadataException("The source database is missing.");
        }

        SnapBoardDatabaseConnectionFactory factory = new(paths.DatabasePath);
        StorageDatabaseSnapshot snapshot;
        await using (SqliteConnection connection = await factory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            await using (SqliteCommand checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using SqliteDataReader reader = await checkpoint
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    reader.GetInt64(0) != 0 ||
                    reader.GetInt64(1) != reader.GetInt64(2))
                {
                    throw new StorageMetadataException(
                        "The source WAL could not be completely checkpointed.");
                }
            }

            snapshot = await VerifyConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await VerifyBlobReferencesAsync(connection, paths.BlobDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        factory.ClearPool();
        DeleteRuntimeFiles(paths.DatabasePath);
        return snapshot;
    }

    public static async ValueTask<StorageDatabaseSnapshot> VerifyDestinationAsync(
        SnapBoardStoragePaths paths,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.DatabasePath) ||
            File.Exists($"{paths.DatabasePath}-wal") ||
            File.Exists($"{paths.DatabasePath}-shm"))
        {
            throw new StorageMetadataException(
                "The staged database is missing or contains runtime WAL files.");
        }

        SnapBoardDatabaseConnectionFactory factory = new(paths.DatabasePath);
        StorageDatabaseSnapshot snapshot;
        await using (SqliteConnection connection = await factory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            snapshot = await VerifyConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await VerifyBlobReferencesAsync(connection, paths.BlobDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        factory.ClearPool();
        DeleteRuntimeFiles(paths.DatabasePath);
        return snapshot;
    }

    private static async ValueTask<StorageDatabaseSnapshot> VerifyConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            object? result = await integrity.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    Convert.ToString(result, CultureInfo.InvariantCulture),
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new StorageMetadataException("SQLite integrity_check failed.");
            }
        }

        int schemaVersion;
        await using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            object? value = await version.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            schemaVersion = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        if (schemaVersion is < 1 or > SnapBoardDatabaseMigrator.CurrentVersion)
        {
            throw new StorageMetadataException("The SQLite schema version is unsupported.");
        }

        long[] itemValues = await ReadAggregateRowAsync(
                connection,
                """
                SELECT
                    COUNT(*),
                    COALESCE(SUM(is_pinned), 0),
                    COALESCE(SUM(is_deleted), 0),
                    COALESCE(SUM(capture_count), 0)
                FROM clipboard_items;
                """,
                expectedColumns: 4,
                cancellationToken)
            .ConfigureAwait(false);
        long[] blobValues = await ReadAggregateRowAsync(
                connection,
                "SELECT COUNT(*), COALESCE(SUM(ref_count), 0) FROM content_blobs;",
                expectedColumns: 2,
                cancellationToken)
            .ConfigureAwait(false);
        bool hasSyncSchema = schemaVersion >= 6;
        return new StorageDatabaseSnapshot(
            schemaVersion,
            itemValues[0],
            itemValues[1],
            itemValues[2],
            itemValues[3],
            await ReadCountAsync(connection, "clipboard_tags", cancellationToken)
                .ConfigureAwait(false),
            await ReadCountAsync(connection, "clipboard_item_tags", cancellationToken)
                .ConfigureAwait(false),
            blobValues[0],
            blobValues[1],
            await ReadCountAsync(connection, "clipboard_representations", cancellationToken)
                .ConfigureAwait(false),
            await ReadCountAsync(connection, "clipboard_files", cancellationToken)
                .ConfigureAwait(false),
            await ReadCountAsync(connection, "clipboard_formats", cancellationToken)
                .ConfigureAwait(false),
            await ReadOptionalCountAsync(
                    connection,
                    "sync_spaces",
                    hasSyncSchema,
                    cancellationToken)
                .ConfigureAwait(false),
            await ReadOptionalCountAsync(
                    connection,
                    "sync_devices",
                    hasSyncSchema,
                    cancellationToken)
                .ConfigureAwait(false),
            await ReadOptionalCountAsync(
                    connection,
                    "sync_outbox",
                    hasSyncSchema,
                    cancellationToken)
                .ConfigureAwait(false),
            await ReadOptionalCountAsync(
                    connection,
                    "sync_inbox",
                    hasSyncSchema,
                    cancellationToken)
                .ConfigureAwait(false),
            await ReadOptionalCountAsync(
                    connection,
                    "sync_checkpoints",
                    hasSyncSchema,
                    cancellationToken)
                .ConfigureAwait(false),
            await ReadOptionalCountAsync(
                    connection,
                    "sync_remote_blobs",
                    hasSyncSchema,
                    cancellationToken)
                .ConfigureAwait(false),
            await ReadOptionalCountAsync(
                    connection,
                    "sync_blob_staging",
                    hasSyncSchema,
                    cancellationToken)
                .ConfigureAwait(false),
            await ReadOptionalCountAsync(
                    connection,
                    "sync_item_state",
                    hasSyncSchema,
                    cancellationToken)
                .ConfigureAwait(false));
    }

    private static async ValueTask VerifyBlobReferencesAsync(
        SqliteConnection connection,
        string blobDirectory,
        CancellationToken cancellationToken)
    {
        string canonicalBlobRoot = Path.GetFullPath(blobDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                blobs.hash,
                blobs.relative_path,
                blobs.size_bytes,
                blobs.ref_count,
                (
                    SELECT COUNT(*)
                    FROM clipboard_representations AS representations
                    WHERE representations.blob_hash = blobs.hash
                ) + (
                    SELECT COUNT(*)
                    FROM clipboard_items AS items
                    WHERE items.thumbnail_blob_hash = blobs.hash
                ) AS actual_ref_count
            FROM content_blobs AS blobs
            ORDER BY blobs.hash;
            """;
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string expectedHash = reader.GetString(0);
            string relativePath = reader.GetString(1);
            long expectedSize = reader.GetInt64(2);
            long storedReferenceCount = reader.GetInt64(3);
            long actualReferenceCount = reader.GetInt64(4);
            if (!IsLowerHexSha256(expectedHash) ||
                Path.IsPathRooted(relativePath) ||
                storedReferenceCount <= 0 ||
                storedReferenceCount != actualReferenceCount)
            {
                throw new StorageMetadataException("A Blob database reference is invalid.");
            }

            string fullPath = Path.GetFullPath(Path.Combine(blobDirectory, relativePath));
            if (!fullPath.StartsWith(canonicalBlobRoot, PathComparison) ||
                !File.Exists(fullPath) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StorageMetadataException("A referenced Blob path is invalid.");
            }

            FileInfo information = new(fullPath);
            if (information.Length != expectedSize)
            {
                throw new StorageMetadataException("A referenced Blob size is invalid.");
            }

            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            string actualHash = Convert.ToHexStringLower(hashBytes);
            CryptographicOperations.ZeroMemory(hashBytes);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new StorageMetadataException("A referenced Blob hash is invalid.");
            }
        }
    }

    private static async ValueTask<long[]> ReadAggregateRowAsync(
        SqliteConnection connection,
        string sql,
        int expectedColumns,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.FieldCount != expectedColumns)
        {
            throw new StorageMetadataException("A database aggregate could not be read.");
        }

        long[] values = new long[expectedColumns];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = reader.GetInt64(index);
        }

        return values;
    }

    private static async ValueTask<long> ReadCountAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        if (!tableName.All(character =>
                character is >= 'a' and <= 'z' or '_'))
        {
            throw new InvalidOperationException("A database table identifier is invalid.");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\";";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static ValueTask<long> ReadOptionalCountAsync(
        SqliteConnection connection,
        string tableName,
        bool exists,
        CancellationToken cancellationToken) => exists
        ? ReadCountAsync(connection, tableName, cancellationToken)
        : ValueTask.FromResult(0L);

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static void DeleteRuntimeFiles(string databasePath)
    {
        File.Delete($"{databasePath}-wal");
        File.Delete($"{databasePath}-shm");
    }
}
