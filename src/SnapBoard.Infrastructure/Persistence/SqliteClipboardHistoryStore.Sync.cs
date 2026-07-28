using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Sync;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Domain.Sync;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Infrastructure.Persistence;

public sealed partial class SqliteClipboardHistoryStore
{
    public async ValueTask ConfigureAsync(
        Guid spaceId,
        Guid deviceId,
        int keyVersion,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ValidateSyncIdentifier(spaceId, nameof(spaceId));
        ValidateSyncIdentifier(deviceId, nameof(deviceId));
        if (keyVersion is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(keyVersion));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                async (connection, token) =>
                {
                    await ConfigureSyncCoreAsync(
                            connection,
                            spaceId,
                            deviceId,
                            keyVersion,
                            enabled,
                            token)
                        .ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<SyncConfigurationSnapshot?> GetConfigurationAsync(
        CancellationToken cancellationToken) => RunReadAsync(
        ReadSyncConfigurationCoreAsync,
        cancellationToken);

    public ValueTask<IReadOnlyList<SyncOutboxItem>> ReadOutboxBatchAsync(
        int maximumCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 100);
        return RunReadAsync(
            (connection, token) => ReadOutboxBatchCoreAsync(
                connection,
                maximumCount,
                now,
                token),
            cancellationToken);
    }

    public async ValueTask MarkOutboxUploadedAsync(
        Guid eventId,
        string? remoteEtag,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken)
    {
        ValidateSyncIdentifier(eventId, nameof(eventId));
        ValidateEtag(remoteEtag);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                (connection, token) => UpdateOutboxUploadedCoreAsync(
                    connection,
                    eventId,
                    remoteEtag,
                    uploadedAt,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask MarkOutboxFailedAsync(
        Guid eventId,
        SyncPersistenceErrorCategory errorCategory,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        ValidateSyncIdentifier(eventId, nameof(eventId));
        if (errorCategory == SyncPersistenceErrorCategory.None ||
            !Enum.IsDefined(errorCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCategory));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                (connection, token) => UpdateOutboxFailedCoreAsync(
                    connection,
                    eventId,
                    errorCategory,
                    nextAttemptAt,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<SyncCheckpointState> GetCheckpointAsync(
        Guid spaceId,
        Guid remoteDeviceId,
        CancellationToken cancellationToken)
    {
        ValidateSyncIdentifier(spaceId, nameof(spaceId));
        ValidateSyncIdentifier(remoteDeviceId, nameof(remoteDeviceId));
        return RunReadAsync(
            (connection, token) => ReadCheckpointCoreAsync(
                connection,
                spaceId,
                remoteDeviceId,
                token),
            cancellationToken);
    }

    public async ValueTask EnsureRemoteDeviceAsync(
        Guid spaceId,
        Guid remoteDeviceId,
        CancellationToken cancellationToken)
    {
        ValidateSyncIdentifier(spaceId, nameof(spaceId));
        ValidateSyncIdentifier(remoteDeviceId, nameof(remoteDeviceId));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                (connection, token) => EnsureRemoteDeviceCoreAsync(
                    connection,
                    spaceId,
                    remoteDeviceId,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<SyncEventApplyResult> ApplyRemoteEventAsync(
        SyncEventEnvelope syncEvent,
        ReadOnlyMemory<byte> serializedEvent,
        string? remoteEtag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(syncEvent);
        ValidateIncomingEvent(syncEvent);
        ValidateEtag(remoteEtag);
        if (serializedEvent.IsEmpty ||
            serializedEvent.Length > SyncProtocol.MaximumEventPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(serializedEvent));
        }

        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            syncEvent,
            SyncJsonContext.Default.SyncEventEnvelope);
        try
        {
            if (!canonical.AsSpan().SequenceEqual(serializedEvent.Span))
            {
                throw new InvalidDataException("A remote sync event is not canonical JSON.");
            }

            string payloadHash = Convert.ToHexStringLower(SHA256.HashData(serializedEvent.Span));
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            RemoteApplyCoreResult applied = await _writeQueue.EnqueueAsync(
                    (connection, token) => ApplyRemoteEventCoreAsync(
                        connection,
                        syncEvent,
                        payloadHash,
                        remoteEtag,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (string relativePath in applied.BlobPathsToDelete)
            {
                try
                {
                    await _blobStore.DeleteAsync(relativePath).ConfigureAwait(false);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return applied.Result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    public async ValueTask<SyncBlobLease?> OpenBlobAsync(
        string plaintextHash,
        CancellationToken cancellationToken)
    {
        ValidateBlobHash(plaintextHash, nameof(plaintextHash));
        return await RunReadAsync(
                (connection, token) => OpenSyncBlobCoreAsync(
                    connection,
                    plaintextHash,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<bool> ContainsBlobAsync(
        string plaintextHash,
        string mediaType,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        ValidateBlobHash(plaintextHash, nameof(plaintextHash));
        ValidateMediaType(mediaType);
        if (sizeBytes is < 0 or > SyncProtocol.MaximumBlobPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        return RunReadAsync(
            (connection, token) => ContainsSyncBlobCoreAsync(
                connection,
                plaintextHash,
                mediaType,
                sizeBytes,
                token),
            cancellationToken);
    }

    public async ValueTask StageDownloadedBlobAsync(
        string plaintextHash,
        string mediaType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ValidateBlobHash(plaintextHash, nameof(plaintextHash));
        ValidateMediaType(mediaType);
        if (content.IsEmpty || content.Length > SyncProtocol.MaximumBlobPlaintextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        string actualHash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        if (!string.Equals(actualHash, plaintextHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The downloaded Blob hash is invalid.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                (connection, token) => StageDownloadedBlobCoreAsync(
                    connection,
                    plaintextHash,
                    mediaType,
                    content,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ConfigureSyncCoreAsync(
        SqliteConnection connection,
        Guid spaceId,
        Guid deviceId,
        int keyVersion,
        bool enabled,
        CancellationToken cancellationToken)
    {
        string space = spaceId.ToString("N");
        string device = deviceId.ToString("N");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            if (enabled)
            {
                await using SqliteCommand disable = connection.CreateCommand();
                disable.Transaction = transaction;
                disable.CommandText = """
                    UPDATE sync_spaces
                    SET is_enabled = 0, updated_at_utc = @now
                    WHERE is_enabled = 1 AND space_id <> @spaceId;
                    """;
                disable.Parameters.AddWithValue("@now", now);
                disable.Parameters.AddWithValue("@spaceId", space);
                await disable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand upsertSpace = connection.CreateCommand())
            {
                upsertSpace.Transaction = transaction;
                upsertSpace.CommandText = """
                    INSERT INTO sync_spaces(
                        space_id, key_version, is_enabled, created_at_utc, updated_at_utc)
                    VALUES (
                        @spaceId, @keyVersion, @enabled, @now, @now)
                    ON CONFLICT(space_id) DO UPDATE SET
                        key_version = excluded.key_version,
                        is_enabled = excluded.is_enabled,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                upsertSpace.Parameters.AddWithValue("@spaceId", space);
                upsertSpace.Parameters.AddWithValue("@keyVersion", keyVersion);
                upsertSpace.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
                upsertSpace.Parameters.AddWithValue("@now", now);
                await upsertSpace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand upsertDevice = connection.CreateCommand())
            {
                upsertDevice.Transaction = transaction;
                upsertDevice.CommandText = """
                    INSERT INTO sync_devices(
                        space_id, device_id, is_local, next_sequence, logical_clock,
                        created_at_utc, updated_at_utc)
                    VALUES (@spaceId, @deviceId, 1, 1, 0, @now, @now)
                    ON CONFLICT(space_id, device_id) DO UPDATE SET
                        is_local = 1,
                        revoked_at_utc = NULL,
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                upsertDevice.Parameters.AddWithValue("@spaceId", space);
                upsertDevice.Parameters.AddWithValue("@deviceId", device);
                upsertDevice.Parameters.AddWithValue("@now", now);
                await upsertDevice.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<SyncConfigurationSnapshot?> ReadSyncConfigurationCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.space_id, d.device_id, s.key_version, s.is_enabled, d.next_sequence
            FROM sync_spaces AS s
            JOIN sync_devices AS d ON d.space_id = s.space_id AND d.is_local = 1
            ORDER BY s.is_enabled DESC, s.updated_at_utc DESC
            LIMIT 1;
            """;
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SyncConfigurationSnapshot(
            ParseCanonicalGuid(reader.GetString(0)),
            ParseCanonicalGuid(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetInt64(3) != 0,
            reader.GetInt64(4));
    }

    private static async ValueTask<IReadOnlyList<SyncOutboxItem>> ReadOutboxBatchCoreAsync(
        SqliteConnection connection,
        int maximumCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.event_id, o.space_id, o.device_id, o.sequence, o.event_json, o.retry_count
            FROM sync_outbox AS o
            JOIN sync_spaces AS s ON s.space_id = o.space_id AND s.is_enabled = 1
            WHERE o.state IN (0, 1) AND o.next_attempt_at_utc <= @now
            ORDER BY o.sequence
            LIMIT @maximumCount;
            """;
        command.Parameters.AddWithValue("@now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@maximumCount", maximumCount);
        List<SyncOutboxItem> result = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            byte[] serializedEvent = reader.GetFieldValue<byte[]>(4);
            SyncEventEnvelope syncEvent;
            try
            {
                syncEvent = JsonSerializer.Deserialize(
                        serializedEvent,
                        SyncJsonContext.Default.SyncEventEnvelope) ??
                    throw new InvalidDataException("An outbox event is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("An outbox event is malformed.", exception);
            }

            if (syncEvent.EventId != ParseCanonicalGuid(reader.GetString(0)) ||
                syncEvent.SpaceId != ParseCanonicalGuid(reader.GetString(1)) ||
                syncEvent.DeviceId != ParseCanonicalGuid(reader.GetString(2)) ||
                syncEvent.Sequence != reader.GetInt64(3))
            {
                throw new InvalidDataException("An outbox event does not match its index.");
            }

            result.Add(new SyncOutboxItem(
                syncEvent,
                serializedEvent,
                reader.GetInt32(5)));
        }

        return result;
    }

    private static async ValueTask<bool> UpdateOutboxUploadedCoreAsync(
        SqliteConnection connection,
        Guid eventId,
        string? remoteEtag,
        DateTimeOffset uploadedAt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_outbox
            SET state = 2,
                event_json = X'',
                last_error_category = NULL,
                remote_etag = @etag,
                uploaded_at_utc = @uploadedAt
            WHERE event_id = @eventId AND state IN (0, 1);
            """;
        command.Parameters.AddWithValue("@eventId", eventId.ToString("N"));
        command.Parameters.AddWithValue("@etag", (object?)remoteEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("@uploadedAt", uploadedAt.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The outbox event is unavailable.");
        }

        return true;
    }

    private static async ValueTask<bool> UpdateOutboxFailedCoreAsync(
        SqliteConnection connection,
        Guid eventId,
        SyncPersistenceErrorCategory errorCategory,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_outbox
            SET state = 0,
                retry_count = retry_count + 1,
                next_attempt_at_utc = @nextAttemptAt,
                last_error_category = @errorCategory
            WHERE event_id = @eventId AND state IN (0, 1);
            """;
        command.Parameters.AddWithValue("@eventId", eventId.ToString("N"));
        command.Parameters.AddWithValue("@nextAttemptAt", nextAttemptAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@errorCategory", errorCategory.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The outbox event is unavailable.");
        }

        return true;
    }

    private static async ValueTask<SyncCheckpointState> ReadCheckpointCoreAsync(
        SqliteConnection connection,
        Guid spaceId,
        Guid remoteDeviceId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT applied_sequence, applied_event_id, remote_etag
            FROM sync_checkpoints
            WHERE space_id = @spaceId AND remote_device_id = @deviceId;
            """;
        command.Parameters.AddWithValue("@spaceId", spaceId.ToString("N"));
        command.Parameters.AddWithValue("@deviceId", remoteDeviceId.ToString("N"));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SyncCheckpointState(spaceId, remoteDeviceId, 0, null, null);
        }

        return new SyncCheckpointState(
            spaceId,
            remoteDeviceId,
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : ParseCanonicalGuid(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async ValueTask<bool> EnsureRemoteDeviceCoreAsync(
        SqliteConnection connection,
        Guid spaceId,
        Guid remoteDeviceId,
        CancellationToken cancellationToken)
    {
        string space = spaceId.ToString("N");
        string device = remoteDeviceId.ToString("N");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            await using (SqliteCommand insertDevice = connection.CreateCommand())
            {
                insertDevice.Transaction = transaction;
                insertDevice.CommandText = """
                    INSERT INTO sync_devices(
                        space_id, device_id, is_local, next_sequence, logical_clock,
                        created_at_utc, updated_at_utc)
                    VALUES (@spaceId, @deviceId, 0, 1, 0, @now, @now)
                    ON CONFLICT(space_id, device_id) DO UPDATE SET
                        updated_at_utc = excluded.updated_at_utc;
                    """;
                insertDevice.Parameters.AddWithValue("@spaceId", space);
                insertDevice.Parameters.AddWithValue("@deviceId", device);
                insertDevice.Parameters.AddWithValue("@now", now);
                await insertDevice.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // checkpoint 与 inbox 在正常写入时同事务提交；行丢失时只允许从连续 inbox 恢复。
            long appliedSequence = 0;
            string? appliedEventId = null;
            await using (SqliteCommand readRecovery = connection.CreateCommand())
            {
                readRecovery.Transaction = transaction;
                readRecovery.CommandText = """
                    SELECT COUNT(*), COALESCE(MIN(sequence), 0), COALESCE(MAX(sequence), 0),
                           (
                               SELECT event_id
                               FROM sync_inbox
                               WHERE space_id = @spaceId AND device_id = @deviceId
                               ORDER BY sequence DESC
                               LIMIT 1
                           )
                    FROM sync_inbox
                    WHERE space_id = @spaceId AND device_id = @deviceId
                      AND NOT EXISTS (
                          SELECT 1
                          FROM sync_checkpoints
                          WHERE space_id = @spaceId AND remote_device_id = @deviceId
                      );
                    """;
                readRecovery.Parameters.AddWithValue("@spaceId", space);
                readRecovery.Parameters.AddWithValue("@deviceId", device);
                await using SqliteDataReader reader = await readRecovery
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    long inboxCount = reader.GetInt64(0);
                    long minimumSequence = reader.GetInt64(1);
                    long maximumSequence = reader.GetInt64(2);
                    if (inboxCount != 0 &&
                        (minimumSequence != 1 || maximumSequence != inboxCount))
                    {
                        throw new InvalidDataException(
                            "The remote sync inbox is not contiguous.");
                    }

                    appliedSequence = maximumSequence;
                    appliedEventId = reader.IsDBNull(3)
                        ? null
                        : ParseCanonicalGuid(reader.GetString(3)).ToString("N");
                }
            }

            await using (SqliteCommand insertCheckpoint = connection.CreateCommand())
            {
                insertCheckpoint.Transaction = transaction;
                insertCheckpoint.CommandText = """
                    INSERT INTO sync_checkpoints(
                        space_id, remote_device_id, applied_sequence, applied_event_id,
                        updated_at_utc)
                    VALUES (@spaceId, @deviceId, @appliedSequence, @appliedEventId, @now)
                    ON CONFLICT(space_id, remote_device_id) DO NOTHING;
                    """;
                insertCheckpoint.Parameters.AddWithValue("@spaceId", space);
                insertCheckpoint.Parameters.AddWithValue("@deviceId", device);
                insertCheckpoint.Parameters.AddWithValue("@appliedSequence", appliedSequence);
                insertCheckpoint.Parameters.AddWithValue(
                    "@appliedEventId",
                    (object?)appliedEventId ?? DBNull.Value);
                insertCheckpoint.Parameters.AddWithValue("@now", now);
                await insertCheckpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<SyncBlobLease?> OpenSyncBlobCoreAsync(
        SqliteConnection connection,
        string plaintextHash,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT relative_path, media_type, size_bytes
            FROM content_blobs
            WHERE hash = @hash;
            """;
        command.Parameters.AddWithValue("@hash", plaintextHash);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        string relativePath = reader.GetString(0);
        string mediaType = reader.GetString(1);
        long expectedSize = reader.GetInt64(2);
        await reader.DisposeAsync().ConfigureAwait(false);
        ReadOnlyMemory<byte> content = await _blobStore.ReadAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            byte[] ownedContent = content.ToArray();
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(ownedContent));
            if (ownedContent.LongLength != expectedSize ||
                !string.Equals(actualHash, plaintextHash, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(ownedContent);
                throw new InvalidDataException("A local sync Blob failed verification.");
            }

            return new SyncBlobLease(plaintextHash, mediaType, ownedContent);
        }
        finally
        {
            ZeroMemory(content);
        }
    }

    private static async ValueTask<bool> ContainsSyncBlobCoreAsync(
        SqliteConnection connection,
        string plaintextHash,
        string mediaType,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM content_blobs
                WHERE hash = @hash AND media_type = @mediaType AND size_bytes = @sizeBytes
                UNION ALL
                SELECT 1
                FROM sync_blob_staging
                WHERE blob_hash = @hash AND media_type = @mediaType AND size_bytes = @sizeBytes
            );
            """;
        command.Parameters.AddWithValue("@hash", plaintextHash);
        command.Parameters.AddWithValue("@mediaType", mediaType);
        command.Parameters.AddWithValue("@sizeBytes", sizeBytes);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    private async ValueTask<bool> StageDownloadedBlobCoreAsync(
        SqliteConnection connection,
        string plaintextHash,
        string mediaType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand existing = connection.CreateCommand())
        {
            existing.CommandText = """
                SELECT media_type, size_bytes
                FROM content_blobs
                WHERE hash = @hash;
                """;
            existing.Parameters.AddWithValue("@hash", plaintextHash);
            await using SqliteDataReader reader = await existing
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(0), mediaType, StringComparison.Ordinal) ||
                    reader.GetInt64(1) != content.Length)
                {
                    throw new InvalidDataException("A downloaded Blob conflicts with local metadata.");
                }

                return true;
            }
        }

        StagedBlob staged = await _blobStore.StageAsync(content, mediaType, cancellationToken)
            .ConfigureAwait(false);
        ReadOnlyMemory<byte> verified = await _blobStore.ReadAsync(
                staged.RelativePath,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (verified.Length != content.Length ||
                !string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(verified.Span)),
                    plaintextHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("A downloaded Blob failed durable verification.");
            }
        }
        finally
        {
            ZeroMemory(verified);
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO sync_blob_staging(
                blob_hash, relative_path, media_type, size_bytes, verified_at_utc)
            VALUES (@hash, @relativePath, @mediaType, @sizeBytes, @verifiedAt)
            ON CONFLICT(blob_hash) DO UPDATE SET
                verified_at_utc = excluded.verified_at_utc
            WHERE sync_blob_staging.relative_path = excluded.relative_path
              AND sync_blob_staging.media_type = excluded.media_type
              AND sync_blob_staging.size_bytes = excluded.size_bytes;
            """;
        insert.Parameters.AddWithValue("@hash", plaintextHash);
        insert.Parameters.AddWithValue("@relativePath", staged.RelativePath);
        insert.Parameters.AddWithValue("@mediaType", mediaType);
        insert.Parameters.AddWithValue("@sizeBytes", content.Length);
        insert.Parameters.AddWithValue(
            "@verifiedAt",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("A downloaded Blob staging record conflicts.");
        }

        return true;
    }

    private static async ValueTask AppendLocalSyncEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncChangeKind changeKind,
        ClipboardItemId itemId,
        string[]? tags,
        bool? isPinned,
        CancellationToken cancellationToken)
    {
        LocalSyncIdentity? identity = await ReadActiveSyncIdentityAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return;
        }

        (long sequence, long logicalTime) = await AllocateLocalSequenceAsync(
                connection,
                transaction,
                identity,
                cancellationToken)
            .ConfigureAwait(false);
        SyncClipboardItemPayload? payload = changeKind == SyncChangeKind.Upsert
            ? await ReadSyncPayloadAsync(connection, transaction, itemId, cancellationToken)
                .ConfigureAwait(false)
            : null;
        Guid eventId = Guid.CreateVersion7();
        SyncEventEnvelope syncEvent = new(
            SyncProtocol.CurrentVersion,
            identity.SpaceId,
            eventId,
            identity.DeviceId,
            sequence,
            logicalTime,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            changeKind,
            itemId.Value,
            payload,
            tags,
            isPinned);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            syncEvent,
            SyncJsonContext.Default.SyncEventEnvelope);
        try
        {
            if (serialized.Length > SyncProtocol.MaximumEventPlaintextBytes)
            {
                throw new InvalidDataException("A local sync event exceeds the protocol limit.");
            }

            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sync_outbox(
                    event_id, space_id, device_id, sequence, event_json,
                    state, retry_count, next_attempt_at_utc, created_at_utc)
                VALUES (
                    @eventId, @spaceId, @deviceId, @sequence, @eventJson,
                    0, 0, @createdAt, @createdAt);
                """;
            insert.Parameters.AddWithValue("@eventId", eventId.ToString("N"));
            insert.Parameters.AddWithValue("@spaceId", identity.SpaceId.ToString("N"));
            insert.Parameters.AddWithValue("@deviceId", identity.DeviceId.ToString("N"));
            insert.Parameters.AddWithValue("@sequence", sequence);
            insert.Parameters.Add("@eventJson", SqliteType.Blob).Value = serialized;
            insert.Parameters.AddWithValue("@createdAt", syncEvent.CreatedAtUnixMilliseconds);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await UpdateLocalItemStateAsync(
                    connection,
                    transaction,
                    identity,
                    itemId,
                    changeKind,
                    logicalTime,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
            ZeroSyncPayload(payload);
        }
    }

    private static async ValueTask AppendLocalSettingSyncEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        if (!SynchronizedSettingRegistry.IsValidValue(key, value))
        {
            throw new InvalidDataException("A synchronized setting value is invalid.");
        }

        LocalSyncIdentity? identity = await ReadActiveSyncIdentityAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return;
        }

        (long sequence, long logicalTime) = await AllocateLocalSequenceAsync(
                connection,
                transaction,
                identity,
                cancellationToken)
            .ConfigureAwait(false);
        Guid eventId = Guid.CreateVersion7();
        SyncEventEnvelope syncEvent = new(
            SyncProtocol.CurrentVersion,
            identity.SpaceId,
            eventId,
            identity.DeviceId,
            sequence,
            logicalTime,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SyncChangeKind.SetSetting,
            Guid.Empty,
            null,
            null,
            null,
            new SyncSettingPayload(key, value));
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            syncEvent,
            SyncJsonContext.Default.SyncEventEnvelope);
        try
        {
            if (serialized.Length > SyncProtocol.MaximumEventPlaintextBytes)
            {
                throw new InvalidDataException("A local setting event exceeds the protocol limit.");
            }

            await using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO sync_outbox(
                        event_id, space_id, device_id, sequence, event_json,
                        state, retry_count, next_attempt_at_utc, created_at_utc)
                    VALUES (
                        @eventId, @spaceId, @deviceId, @sequence, @eventJson,
                        0, 0, @createdAt, @createdAt);
                    """;
                insert.Parameters.AddWithValue("@eventId", eventId.ToString("N"));
                insert.Parameters.AddWithValue("@spaceId", identity.SpaceId.ToString("N"));
                insert.Parameters.AddWithValue("@deviceId", identity.DeviceId.ToString("N"));
                insert.Parameters.AddWithValue("@sequence", sequence);
                insert.Parameters.Add("@eventJson", SqliteType.Blob).Value = serialized;
                insert.Parameters.AddWithValue("@createdAt", syncEvent.CreatedAtUnixMilliseconds);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteCommand state = connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO sync_setting_state(
                    space_id, setting_key, logical_time, device_id)
                VALUES (@spaceId, @key, @logicalTime, @deviceId)
                ON CONFLICT(space_id, setting_key) DO UPDATE SET
                    logical_time = excluded.logical_time,
                    device_id = excluded.device_id;
                """;
            state.Parameters.AddWithValue("@spaceId", identity.SpaceId.ToString("N"));
            state.Parameters.AddWithValue("@key", key);
            state.Parameters.AddWithValue("@logicalTime", logicalTime);
            state.Parameters.AddWithValue("@deviceId", identity.DeviceId.ToString("N"));
            await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private static async ValueTask<LocalSyncIdentity?> ReadActiveSyncIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.space_id, d.device_id, s.key_version
            FROM sync_spaces AS s
            JOIN sync_devices AS d ON d.space_id = s.space_id AND d.is_local = 1
            WHERE s.is_enabled = 1 AND d.revoked_at_utc IS NULL
            LIMIT 1;
            """;
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new LocalSyncIdentity(
                ParseCanonicalGuid(reader.GetString(0)),
                ParseCanonicalGuid(reader.GetString(1)),
                reader.GetInt32(2))
            : null;
    }

    private static async ValueTask<(long Sequence, long LogicalTime)>
        AllocateLocalSequenceAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            LocalSyncIdentity identity,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sync_devices
            SET next_sequence = next_sequence + 1,
                logical_clock = logical_clock + 1,
                updated_at_utc = @now
            WHERE space_id = @spaceId AND device_id = @deviceId
              AND is_local = 1 AND revoked_at_utc IS NULL
            RETURNING next_sequence - 1, logical_clock;
            """;
        command.Parameters.AddWithValue("@spaceId", identity.SpaceId.ToString("N"));
        command.Parameters.AddWithValue("@deviceId", identity.DeviceId.ToString("N"));
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The local sync device is unavailable.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async ValueTask<SyncClipboardItemPayload> ReadSyncPayloadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand itemCommand = connection.CreateCommand();
        itemCommand.Transaction = transaction;
        itemCommand.CommandText = """
            SELECT content_hash, primary_kind, display_category, captured_at_utc,
                   preview_text, searchable_text, source_process_name,
                   source_application_user_model_id, source_package_family_name,
                   source_attribution_kind, thumbnail_blob_hash, total_size_bytes
            FROM clipboard_items
            WHERE id = @itemId AND is_deleted = 0;
            """;
        itemCommand.Parameters.AddWithValue("@itemId", itemId.ToString());
        string contentHash;
        SyncPayloadKind primaryKind;
        int displayCategory;
        long capturedAt;
        string previewText;
        string searchableText;
        string? sourceApplication;
        string? sourceApplicationUserModelId;
        string? sourcePackageFamilyName;
        int sourceAttributionKind;
        string? thumbnailHash;
        long totalSizeBytes;
        await using (SqliteDataReader itemReader = await itemCommand
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!await itemReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("A local sync item is unavailable.");
            }

            contentHash = itemReader.GetString(0);
            primaryKind = ParsePayloadKind(itemReader.GetInt32(1));
            displayCategory = itemReader.GetInt32(2);
            capturedAt = itemReader.GetInt64(3);
            previewText = itemReader.GetString(4);
            searchableText = itemReader.GetString(5);
            sourceApplication = itemReader.IsDBNull(6) ? null : itemReader.GetString(6);
            sourceApplicationUserModelId = itemReader.IsDBNull(7)
                ? null
                : itemReader.GetString(7);
            sourcePackageFamilyName = itemReader.IsDBNull(8)
                ? null
                : itemReader.GetString(8);
            sourceAttributionKind = itemReader.GetInt32(9);
            thumbnailHash = itemReader.IsDBNull(10) ? null : itemReader.GetString(10);
            totalSizeBytes = itemReader.GetInt64(11);
        }

        List<SyncRepresentationPayload> representations = [];
        if (primaryKind != SyncPayloadKind.FileReference)
        {
            await using SqliteCommand representationCommand = connection.CreateCommand();
            representationCommand.Transaction = transaction;
            representationCommand.CommandText = """
                SELECT kind, media_type, inline_text, inline_data, blob_hash,
                       size_bytes, bitmap_encoding, width, height, bits_per_pixel
                FROM clipboard_representations
                WHERE item_id = @itemId
                ORDER BY kind;
                """;
            representationCommand.Parameters.AddWithValue("@itemId", itemId.ToString());
            await using SqliteDataReader reader = await representationCommand
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (representations.Count >= SyncProtocol.MaximumRepresentationsPerItem)
                {
                    throw new InvalidDataException("A sync item has too many representations.");
                }

                SyncPayloadKind kind = ParsePayloadKind(reader.GetInt32(0));
                if (kind == SyncPayloadKind.FileReference)
                {
                    continue;
                }

                representations.Add(new SyncRepresentationPayload(
                    kind,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.GetInt32(9)));
            }
        }

        SyncBlobReferencePayload? thumbnail = thumbnailHash is null
            ? null
            : await ReadBlobReferenceAsync(
                    connection,
                    transaction,
                    thumbnailHash,
                    cancellationToken)
                .ConfigureAwait(false);
        bool fileReference = primaryKind == SyncPayloadKind.FileReference;
        return new SyncClipboardItemPayload(
            contentHash,
            primaryKind,
            displayCategory,
            capturedAt,
            fileReference ? SyncProtocol.FileReferencePreview : previewText,
            fileReference ? string.Empty : searchableText,
            sourceApplication,
            sourceApplicationUserModelId,
            sourcePackageFamilyName,
            sourceAttributionKind,
            representations.ToArray(),
            thumbnail,
            fileReference ? 0 : totalSizeBytes);
    }

    private static async ValueTask<SyncBlobReferencePayload> ReadBlobReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hash,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT media_type, size_bytes
            FROM content_blobs
            WHERE hash = @hash;
            """;
        command.Parameters.AddWithValue("@hash", hash);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("A sync Blob reference is unavailable.");
        }

        return new SyncBlobReferencePayload(hash, reader.GetString(0), reader.GetInt64(1));
    }

    private static async ValueTask UpdateLocalItemStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalSyncIdentity identity,
        ClipboardItemId itemId,
        SyncChangeKind changeKind,
        long logicalTime,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText = """
                INSERT INTO sync_item_state(space_id, item_id)
                VALUES (@spaceId, @itemId)
                ON CONFLICT(space_id, item_id) DO NOTHING;
                """;
            ensure.Parameters.AddWithValue("@spaceId", identity.SpaceId.ToString("N"));
            ensure.Parameters.AddWithValue("@itemId", itemId.ToString());
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string updateSql = changeKind switch
        {
            SyncChangeKind.Upsert => """
                UPDATE sync_item_state
                SET content_logical_time = @logicalTime, content_device_id = @deviceId
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            SyncChangeKind.SetTags => """
                UPDATE sync_item_state
                SET tags_logical_time = @logicalTime, tags_device_id = @deviceId
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            SyncChangeKind.SetPinned => """
                UPDATE sync_item_state
                SET pin_logical_time = @logicalTime, pin_device_id = @deviceId
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            SyncChangeKind.Delete => """
                UPDATE sync_item_state
                SET delete_logical_time = @logicalTime, delete_device_id = @deviceId,
                    is_deleted = 1
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            SyncChangeKind.Restore => """
                UPDATE sync_item_state
                SET delete_logical_time = @logicalTime, delete_device_id = @deviceId,
                    is_deleted = 0
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(changeKind)),
        };
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = updateSql;
        update.Parameters.AddWithValue("@logicalTime", logicalTime);
        update.Parameters.AddWithValue("@deviceId", identity.DeviceId.ToString("N"));
        update.Parameters.AddWithValue("@spaceId", identity.SpaceId.ToString("N"));
        update.Parameters.AddWithValue("@itemId", itemId.ToString());
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RemoteApplyCoreResult> ApplyRemoteEventCoreAsync(
        SqliteConnection connection,
        SyncEventEnvelope syncEvent,
        string payloadHash,
        string? remoteEtag,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            await ValidateRemoteDeviceAsync(
                    connection,
                    transaction,
                    syncEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncCheckpointState checkpoint = await ReadCheckpointForApplyAsync(
                    connection,
                    transaction,
                    syncEvent.SpaceId,
                    syncEvent.DeviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            long expectedSequence = checked(checkpoint.AppliedSequence + 1);
            if (syncEvent.Sequence <= checkpoint.AppliedSequence)
            {
                bool exactDuplicate = await IsExactInboxDuplicateAsync(
                        connection,
                        transaction,
                        syncEvent,
                        payloadHash,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!exactDuplicate)
                {
                    throw new InvalidDataException(
                        "A remote sequence was replayed with different content.");
                }

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new RemoteApplyCoreResult(
                    new SyncEventApplyResult(
                        SyncEventApplyStatus.Duplicate,
                        expectedSequence),
                    []);
            }

            if (syncEvent.Sequence != expectedSequence)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new RemoteApplyCoreResult(
                    new SyncEventApplyResult(
                        SyncEventApplyStatus.SequenceGap,
                        expectedSequence),
                    []);
            }

            SyncLogicalVersion incomingVersion = new(
                syncEvent.LogicalTimestamp,
                syncEvent.DeviceId);
            IReadOnlyList<string> blobPathsToDelete = [];
            bool shouldApply;
            if (syncEvent.ChangeKind == SyncChangeKind.SetSetting)
            {
                SyncLogicalVersion? currentVersion = await ReadSettingLogicalVersionAsync(
                        connection,
                        transaction,
                        syncEvent.SpaceId,
                        syncEvent.Setting!.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                shouldApply = incomingVersion.IsNewerThan(currentVersion);
                if (shouldApply)
                {
                    await ApplyRemoteSettingAsync(
                            connection,
                            transaction,
                            syncEvent,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                SyncItemConflictState conflictState = await ReadItemConflictStateAsync(
                        connection,
                        transaction,
                        syncEvent.SpaceId,
                        syncEvent.ItemId,
                        cancellationToken)
                    .ConfigureAwait(false);
                SyncMutationKind mutation = MapMutation(syncEvent.ChangeKind);
                shouldApply = SyncConflictRules.ShouldApply(
                    conflictState,
                    mutation,
                    incomingVersion);
                if (shouldApply)
                {
                    blobPathsToDelete = await ApplyRemoteMutationAsync(
                            connection,
                            transaction,
                            syncEvent,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await UpdateRemoteItemStateAsync(
                            connection,
                            transaction,
                            syncEvent,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await RecordInboxAndCheckpointAsync(
                    connection,
                    transaction,
                    syncEvent,
                    payloadHash,
                    remoteEtag,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RemoteApplyCoreResult(
                new SyncEventApplyResult(
                    shouldApply
                        ? SyncEventApplyStatus.Applied
                        : SyncEventApplyStatus.ConflictIgnored,
                    checked(syncEvent.Sequence + 1)),
                blobPathsToDelete);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask ValidateRemoteDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEventEnvelope syncEvent,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT d.is_local, d.revoked_at_utc, s.is_enabled
            FROM sync_devices AS d
            JOIN sync_spaces AS s ON s.space_id = d.space_id
            WHERE d.space_id = @spaceId AND d.device_id = @deviceId;
            """;
        command.Parameters.AddWithValue("@spaceId", syncEvent.SpaceId.ToString("N"));
        command.Parameters.AddWithValue("@deviceId", syncEvent.DeviceId.ToString("N"));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != 0 || !reader.IsDBNull(1) || reader.GetInt64(2) == 0)
        {
            throw new InvalidOperationException("The remote sync device is unavailable.");
        }
    }

    private static async ValueTask<SyncCheckpointState> ReadCheckpointForApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid spaceId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT applied_sequence, applied_event_id, remote_etag
            FROM sync_checkpoints
            WHERE space_id = @spaceId AND remote_device_id = @deviceId;
            """;
        command.Parameters.AddWithValue("@spaceId", spaceId.ToString("N"));
        command.Parameters.AddWithValue("@deviceId", deviceId.ToString("N"));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The remote sync checkpoint is unavailable.");
        }

        return new SyncCheckpointState(
            spaceId,
            deviceId,
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : ParseCanonicalGuid(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async ValueTask<bool> IsExactInboxDuplicateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEventEnvelope syncEvent,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, payload_hash
            FROM sync_inbox
            WHERE space_id = @spaceId AND device_id = @deviceId AND sequence = @sequence;
            """;
        command.Parameters.AddWithValue("@spaceId", syncEvent.SpaceId.ToString("N"));
        command.Parameters.AddWithValue("@deviceId", syncEvent.DeviceId.ToString("N"));
        command.Parameters.AddWithValue("@sequence", syncEvent.Sequence);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            string.Equals(
                reader.GetString(0),
                syncEvent.EventId.ToString("N"),
                StringComparison.Ordinal) &&
            string.Equals(reader.GetString(1), payloadHash, StringComparison.Ordinal);
    }

    private static async ValueTask<SyncItemConflictState> ReadItemConflictStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid spaceId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT is_deleted,
                   content_logical_time, content_device_id,
                   tags_logical_time, tags_device_id,
                   pin_logical_time, pin_device_id,
                   delete_logical_time, delete_device_id
            FROM sync_item_state
            WHERE space_id = @spaceId AND item_id = @itemId;
            """;
        command.Parameters.AddWithValue("@spaceId", spaceId.ToString("N"));
        command.Parameters.AddWithValue("@itemId", itemId.ToString("D"));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SyncItemConflictState(IsDeleted: false);
        }

        return new SyncItemConflictState(
            reader.GetInt64(0) != 0,
            ReadLogicalVersion(reader, 1, 2),
            ReadLogicalVersion(reader, 3, 4),
            ReadLogicalVersion(reader, 5, 6),
            ReadLogicalVersion(reader, 7, 8));
    }

    private static SyncLogicalVersion? ReadLogicalVersion(
        SqliteDataReader reader,
        int logicalTimeOrdinal,
        int deviceOrdinal)
    {
        long logicalTime = reader.GetInt64(logicalTimeOrdinal);
        if (logicalTime == 0)
        {
            if (!reader.IsDBNull(deviceOrdinal))
            {
                throw new InvalidDataException("A sync conflict version is inconsistent.");
            }

            return null;
        }

        if (reader.IsDBNull(deviceOrdinal))
        {
            throw new InvalidDataException("A sync conflict version has no device.");
        }

        return new SyncLogicalVersion(
            logicalTime,
            ParseCanonicalGuid(reader.GetString(deviceOrdinal)));
    }

    private static async ValueTask<SyncLogicalVersion?> ReadSettingLogicalVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid spaceId,
        string key,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT logical_time, device_id
            FROM sync_setting_state
            WHERE space_id = @spaceId AND setting_key = @key;
            """;
        command.Parameters.AddWithValue("@spaceId", spaceId.ToString("N"));
        command.Parameters.AddWithValue("@key", key);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SyncLogicalVersion(
            reader.GetInt64(0),
            ParseCanonicalGuid(reader.GetString(1)));
    }

    private static async ValueTask ApplyRemoteSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEventEnvelope syncEvent,
        CancellationToken cancellationToken)
    {
        SyncSettingPayload setting = syncEvent.Setting!;
        await SetSettingCoreAsync(
                connection,
                transaction,
                setting.Key,
                setting.Value,
                cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            INSERT INTO sync_setting_state(
                space_id, setting_key, logical_time, device_id)
            VALUES (@spaceId, @key, @logicalTime, @deviceId)
            ON CONFLICT(space_id, setting_key) DO UPDATE SET
                logical_time = excluded.logical_time,
                device_id = excluded.device_id;
            """;
        state.Parameters.AddWithValue("@spaceId", syncEvent.SpaceId.ToString("N"));
        state.Parameters.AddWithValue("@key", setting.Key);
        state.Parameters.AddWithValue("@logicalTime", syncEvent.LogicalTimestamp);
        state.Parameters.AddWithValue("@deviceId", syncEvent.DeviceId.ToString("N"));
        await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<string>> ApplyRemoteMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEventEnvelope syncEvent,
        CancellationToken cancellationToken)
    {
        ClipboardItemId itemId = new(syncEvent.ItemId);
        switch (syncEvent.ChangeKind)
        {
            case SyncChangeKind.Upsert:
                await ApplyRemoteUpsertAsync(
                        connection,
                        transaction,
                        syncEvent,
                        restore: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                return [];
            case SyncChangeKind.SetTags:
                await ApplyRemoteTagsAsync(
                        connection,
                        transaction,
                        itemId,
                        syncEvent.Tags!,
                        cancellationToken)
                    .ConfigureAwait(false);
                return [];
            case SyncChangeKind.SetPinned:
                await ApplyRemotePinAsync(
                        connection,
                        transaction,
                        itemId,
                        syncEvent.IsPinned!.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                return [];
            case SyncChangeKind.Delete:
                return await TombstoneRemoteItemAsync(
                        connection,
                        transaction,
                        itemId,
                        syncEvent.CreatedAtUnixMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            case SyncChangeKind.Restore:
                await ApplyRemoteUpsertAsync(
                        connection,
                        transaction,
                        syncEvent,
                        restore: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                return [];
            default:
                throw new InvalidDataException("The remote sync change kind is invalid.");
        }
    }

    private static async ValueTask ApplyRemoteUpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEventEnvelope syncEvent,
        bool restore,
        CancellationToken cancellationToken)
    {
        SyncClipboardItemPayload payload = syncEvent.Item!;
        ClipboardItemId itemId = new(syncEvent.ItemId);
        await using (SqliteCommand existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT content_hash, is_deleted
                FROM clipboard_items
                WHERE id = @itemId;
                """;
            existing.Parameters.AddWithValue("@itemId", itemId.ToString());
            await using SqliteDataReader reader = await existing
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string existingHash = reader.GetString(0);
                bool isDeleted = reader.GetInt64(1) != 0;
                if (!isDeleted)
                {
                    if (!string.Equals(
                            existingHash,
                            payload.ContentHash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "An immutable sync item identifier has conflicting content.");
                    }

                    return;
                }

                if (!restore)
                {
                    throw new InvalidDataException("A deleted sync item cannot be upserted.");
                }
            }
            else if (restore)
            {
                throw new InvalidDataException("A restore event has no local tombstone.");
            }
        }

        if (restore)
        {
            await using SqliteCommand removeTombstone = connection.CreateCommand();
            removeTombstone.Transaction = transaction;
            removeTombstone.CommandText =
                "DELETE FROM clipboard_items WHERE id = @itemId AND is_deleted = 1;";
            removeTombstone.Parameters.AddWithValue("@itemId", itemId.ToString());
            if (await removeTombstone.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("A restore tombstone changed concurrently.");
            }
        }

        DateTimeOffset capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            payload.CapturedAtUnixMilliseconds);
        List<PreparedRepresentation> prepared = [];
        List<StagedBlob> blobReferences = [];
        try
        {
            foreach (SyncRepresentationPayload representation in payload.Representations)
            {
                ClipboardCapturedRepresentation source = new(
                    (ClipboardContentKind)(int)representation.Kind,
                    representation.MediaType,
                    representation.Text,
                    representation.InlineData ?? ReadOnlyMemory<byte>.Empty,
                    representation.BitmapEncoding is null
                        ? null
                        : (ClipboardStoredBitmapEncoding)representation.BitmapEncoding.Value,
                    representation.Width,
                    representation.Height,
                    checked((ushort)representation.BitsPerPixel));
                StagedBlob? blob = representation.BlobHash is null
                    ? null
                    : await ReadAvailableBlobAsync(
                            connection,
                            transaction,
                            representation.BlobHash,
                            representation.MediaType,
                            representation.SizeBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (blob is not null)
                {
                    blobReferences.Add(blob);
                }

                prepared.Add(new PreparedRepresentation(
                    source,
                    representation.Text,
                    representation.InlineData?.ToArray(),
                    blob,
                    representation.SizeBytes));
            }

            StagedBlob? thumbnail = payload.Thumbnail is null
                ? null
                : await ReadAvailableBlobAsync(
                        connection,
                        transaction,
                        payload.Thumbnail.Hash,
                        payload.Thumbnail.MediaType,
                        payload.Thumbnail.SizeBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (thumbnail is not null)
            {
                blobReferences.Add(thumbnail);
            }

            ClipboardCapturedItem item = new()
            {
                Id = itemId,
                SequenceNumber = checked((ulong)syncEvent.Sequence),
                CapturedAt = capturedAt,
                SourceProcessName = payload.SourceApplication,
                SourceApplicationUserModelId = payload.SourceApplicationUserModelId,
                SourcePackageFamilyName = payload.SourcePackageFamilyName,
                SourceAccessStatus = 0,
                SourceAttributionKind = payload.SourceAttributionKind,
                ContentHash = new ClipboardContentHash(payload.ContentHash),
                PrimaryKind = (ClipboardContentKind)(int)payload.PrimaryKind,
                DisplayCategory = (ClipboardHistoryDisplayCategory)payload.DisplayCategory,
                PreviewText = payload.PreviewText,
                SearchableText = payload.SearchableText,
                Representations = [],
                FilePaths = [],
                Formats = [],
                TotalSizeBytes = payload.TotalSizeBytes,
            };
            foreach (StagedBlob blob in blobReferences)
            {
                await AddBlobReferenceAsync(
                        connection,
                        transaction,
                        blob,
                        capturedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await InsertItemAsync(
                    connection,
                    transaction,
                    item,
                    thumbnail?.Hash,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertRepresentationsAsync(
                    connection,
                    transaction,
                    itemId,
                    prepared,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertFtsAsync(
                    connection,
                    transaction,
                    itemId,
                    payload.SearchableText,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (string hash in blobReferences
                .Select(blob => blob.Hash)
                .Distinct(StringComparer.Ordinal))
            {
                await using SqliteCommand consumeStaging = connection.CreateCommand();
                consumeStaging.Transaction = transaction;
                consumeStaging.CommandText =
                    "DELETE FROM sync_blob_staging WHERE blob_hash = @hash;";
                consumeStaging.Parameters.AddWithValue("@hash", hash);
                await consumeStaging.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ZeroInlineBinaryCopies(prepared);
        }
    }

    private static async ValueTask<StagedBlob> ReadAvailableBlobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hash,
        string mediaType,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT relative_path, media_type, size_bytes
            FROM content_blobs
            WHERE hash = @hash
            UNION ALL
            SELECT relative_path, media_type, size_bytes
            FROM sync_blob_staging
            WHERE blob_hash = @hash
              AND NOT EXISTS (SELECT 1 FROM content_blobs WHERE hash = @hash)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@hash", hash);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !string.Equals(reader.GetString(1), mediaType, StringComparison.Ordinal) ||
            reader.GetInt64(2) != sizeBytes)
        {
            throw new InvalidDataException("A referenced remote Blob is not staged.");
        }

        return new StagedBlob(
            hash,
            reader.GetString(0),
            mediaType,
            sizeBytes,
            CreatedNew: false);
    }

    private static async ValueTask ApplyRemoteTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        await EnsureActiveItemExistsAsync(
                connection,
                transaction,
                itemId,
                cancellationToken)
            .ConfigureAwait(false);
        await using (SqliteCommand clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM clipboard_item_tags WHERE item_id = @itemId;";
            clear.Parameters.AddWithValue("@itemId", itemId.ToString());
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (string tag in tags)
        {
            long tagId;
            await using (SqliteCommand upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO clipboard_tags(name, normalized_name, created_at_utc)
                    VALUES (@name, @normalizedName, @createdAt)
                    ON CONFLICT(normalized_name) DO UPDATE SET name = excluded.name
                    RETURNING id;
                    """;
                upsert.Parameters.AddWithValue("@name", tag);
                upsert.Parameters.AddWithValue("@normalizedName", tag.ToUpperInvariant());
                upsert.Parameters.AddWithValue(
                    "@createdAt",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                tagId = Convert.ToInt64(
                    await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            await using SqliteCommand assign = connection.CreateCommand();
            assign.Transaction = transaction;
            assign.CommandText = """
                INSERT INTO clipboard_item_tags(item_id, tag_id)
                VALUES (@itemId, @tagId);
                """;
            assign.Parameters.AddWithValue("@itemId", itemId.ToString());
            assign.Parameters.AddWithValue("@tagId", tagId);
            await assign.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = """
            DELETE FROM clipboard_tags
            WHERE NOT EXISTS (
                SELECT 1 FROM clipboard_item_tags it WHERE it.tag_id = clipboard_tags.id
            );
            """;
        await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ApplyRemotePinAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        bool isPinned,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE clipboard_items
            SET is_pinned = @isPinned, updated_at_utc = @updatedAt
            WHERE id = @itemId AND is_deleted = 0;
            """;
        command.Parameters.AddWithValue("@isPinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue(
            "@updatedAt",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@itemId", itemId.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("A remote pin event references a missing item.");
        }
    }

    private static async ValueTask EnsureActiveItemExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM clipboard_items WHERE id = @itemId AND is_deleted = 0
            );
            """;
        command.Parameters.AddWithValue("@itemId", itemId.ToString());
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 0)
        {
            throw new InvalidDataException("A remote event references a missing item.");
        }
    }

    private static async ValueTask<IReadOnlyList<string>> TombstoneRemoteItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardItemId itemId,
        long deletedAtUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM clipboard_items WHERE id = @itemId AND is_deleted = 0
                );
                """;
            exists.Parameters.AddWithValue("@itemId", itemId.ToString());
            if (Convert.ToInt32(
                    await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) == 0)
            {
                return [];
            }
        }

        List<BlobReferenceRemoval> references = [];
        await using (SqliteCommand readReferences = connection.CreateCommand())
        {
            readReferences.Transaction = transaction;
            readReferences.CommandText = """
                WITH removed_references(blob_hash) AS (
                    SELECT blob_hash FROM clipboard_representations
                    WHERE item_id = @itemId AND blob_hash IS NOT NULL
                    UNION ALL
                    SELECT thumbnail_blob_hash FROM clipboard_items
                    WHERE id = @itemId AND thumbnail_blob_hash IS NOT NULL
                )
                SELECT b.hash, b.relative_path, b.ref_count, COUNT(*)
                FROM removed_references r
                JOIN content_blobs b ON b.hash = r.blob_hash
                GROUP BY b.hash, b.relative_path, b.ref_count;
                """;
            readReferences.Parameters.AddWithValue("@itemId", itemId.ToString());
            await using SqliteDataReader reader = await readReferences
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                references.Add(new BlobReferenceRemoval(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)));
            }
        }

        foreach (string table in new[]
        {
            "clipboard_items_fts",
            "clipboard_item_tags",
            "clipboard_representations",
            "clipboard_files",
            "clipboard_formats",
        })
        {
            await using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = table == "clipboard_items_fts"
                ? "DELETE FROM clipboard_items_fts WHERE item_id = @itemId;"
                : $"DELETE FROM \"{table}\" WHERE item_id = @itemId;";
            delete.Parameters.AddWithValue("@itemId", itemId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

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
                WHERE id = @itemId AND is_deleted = 0;
                """;
            tombstone.Parameters.AddWithValue("@itemId", itemId.ToString());
            tombstone.Parameters.AddWithValue("@deletedAt", deletedAtUnixMilliseconds);
            if (await tombstone.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("A remote tombstone changed concurrently.");
            }
        }

        List<string> pathsToDelete = [];
        foreach (BlobReferenceRemoval reference in references)
        {
            if (reference.ExistingReferenceCount < reference.RemovedReferenceCount)
            {
                throw new InvalidDataException("A remote Blob reference count is inconsistent.");
            }

            await using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            if (reference.ExistingReferenceCount == reference.RemovedReferenceCount)
            {
                update.CommandText = """
                    DELETE FROM content_blobs
                    WHERE hash = @hash AND ref_count = @existingReferenceCount;
                    """;
                pathsToDelete.Add(reference.RelativePath);
            }
            else
            {
                update.CommandText = """
                    UPDATE content_blobs
                    SET ref_count = ref_count - @removedReferenceCount
                    WHERE hash = @hash AND ref_count = @existingReferenceCount;
                    """;
                update.Parameters.AddWithValue(
                    "@removedReferenceCount",
                    reference.RemovedReferenceCount);
            }

            update.Parameters.AddWithValue("@hash", reference.Hash);
            update.Parameters.AddWithValue(
                "@existingReferenceCount",
                reference.ExistingReferenceCount);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("A remote Blob reference update failed.");
            }
        }

        await using SqliteCommand cleanupTags = connection.CreateCommand();
        cleanupTags.Transaction = transaction;
        cleanupTags.CommandText = """
            DELETE FROM clipboard_tags
            WHERE NOT EXISTS (
                SELECT 1 FROM clipboard_item_tags it WHERE it.tag_id = clipboard_tags.id
            );
            """;
        await cleanupTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return pathsToDelete;
    }

    private static async ValueTask UpdateRemoteItemStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEventEnvelope syncEvent,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText = """
                INSERT INTO sync_item_state(space_id, item_id)
                VALUES (@spaceId, @itemId)
                ON CONFLICT(space_id, item_id) DO NOTHING;
                """;
            ensure.Parameters.AddWithValue("@spaceId", syncEvent.SpaceId.ToString("N"));
            ensure.Parameters.AddWithValue("@itemId", syncEvent.ItemId.ToString("D"));
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string sql = syncEvent.ChangeKind switch
        {
            SyncChangeKind.Upsert => """
                UPDATE sync_item_state
                SET content_logical_time = @logicalTime, content_device_id = @deviceId
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            SyncChangeKind.SetTags => """
                UPDATE sync_item_state
                SET tags_logical_time = @logicalTime, tags_device_id = @deviceId
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            SyncChangeKind.SetPinned => """
                UPDATE sync_item_state
                SET pin_logical_time = @logicalTime, pin_device_id = @deviceId
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            SyncChangeKind.Delete => """
                UPDATE sync_item_state
                SET delete_logical_time = @logicalTime, delete_device_id = @deviceId,
                    is_deleted = 1
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            SyncChangeKind.Restore => """
                UPDATE sync_item_state
                SET delete_logical_time = @logicalTime, delete_device_id = @deviceId,
                    content_logical_time = @logicalTime, content_device_id = @deviceId,
                    is_deleted = 0
                WHERE space_id = @spaceId AND item_id = @itemId;
                """,
            _ => throw new InvalidDataException("A remote sync state change is invalid."),
        };
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = sql;
        update.Parameters.AddWithValue("@logicalTime", syncEvent.LogicalTimestamp);
        update.Parameters.AddWithValue("@deviceId", syncEvent.DeviceId.ToString("N"));
        update.Parameters.AddWithValue("@spaceId", syncEvent.SpaceId.ToString("N"));
        update.Parameters.AddWithValue("@itemId", syncEvent.ItemId.ToString("D"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RecordInboxAndCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncEventEnvelope syncEvent,
        string payloadHash,
        string? remoteEtag,
        CancellationToken cancellationToken)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using (SqliteCommand inbox = connection.CreateCommand())
        {
            inbox.Transaction = transaction;
            inbox.CommandText = """
                INSERT INTO sync_inbox(
                    space_id, device_id, event_id, sequence, payload_hash, applied_at_utc)
                VALUES (@spaceId, @deviceId, @eventId, @sequence, @payloadHash, @now);
                """;
            inbox.Parameters.AddWithValue("@spaceId", syncEvent.SpaceId.ToString("N"));
            inbox.Parameters.AddWithValue("@deviceId", syncEvent.DeviceId.ToString("N"));
            inbox.Parameters.AddWithValue("@eventId", syncEvent.EventId.ToString("N"));
            inbox.Parameters.AddWithValue("@sequence", syncEvent.Sequence);
            inbox.Parameters.AddWithValue("@payloadHash", payloadHash);
            inbox.Parameters.AddWithValue("@now", now);
            await inbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                UPDATE sync_checkpoints
                SET applied_sequence = @sequence,
                    applied_event_id = @eventId,
                    remote_etag = @etag,
                    updated_at_utc = @now
                WHERE space_id = @spaceId AND remote_device_id = @deviceId
                  AND applied_sequence = @previousSequence;
                """;
            checkpoint.Parameters.AddWithValue("@sequence", syncEvent.Sequence);
            checkpoint.Parameters.AddWithValue("@eventId", syncEvent.EventId.ToString("N"));
            checkpoint.Parameters.AddWithValue("@etag", (object?)remoteEtag ?? DBNull.Value);
            checkpoint.Parameters.AddWithValue("@now", now);
            checkpoint.Parameters.AddWithValue("@spaceId", syncEvent.SpaceId.ToString("N"));
            checkpoint.Parameters.AddWithValue("@deviceId", syncEvent.DeviceId.ToString("N"));
            checkpoint.Parameters.AddWithValue("@previousSequence", syncEvent.Sequence - 1);
            if (await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException("The remote checkpoint changed concurrently.");
            }
        }

        await using SqliteCommand clocks = connection.CreateCommand();
        clocks.Transaction = transaction;
        clocks.CommandText = """
            UPDATE sync_devices
            SET logical_clock = MAX(logical_clock, @logicalTime),
                updated_at_utc = @now
            WHERE space_id = @spaceId
              AND (is_local = 1 OR device_id = @remoteDeviceId);
            """;
        clocks.Parameters.AddWithValue("@logicalTime", syncEvent.LogicalTimestamp);
        clocks.Parameters.AddWithValue("@now", now);
        clocks.Parameters.AddWithValue("@spaceId", syncEvent.SpaceId.ToString("N"));
        clocks.Parameters.AddWithValue("@remoteDeviceId", syncEvent.DeviceId.ToString("N"));
        await clocks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SyncMutationKind MapMutation(SyncChangeKind changeKind) =>
        changeKind switch
        {
            SyncChangeKind.Upsert => SyncMutationKind.Content,
            SyncChangeKind.SetTags => SyncMutationKind.Tags,
            SyncChangeKind.SetPinned => SyncMutationKind.Pin,
            SyncChangeKind.Delete => SyncMutationKind.Delete,
            SyncChangeKind.Restore => SyncMutationKind.Restore,
            _ => throw new InvalidDataException("A remote sync change kind is invalid."),
        };

    private static void ValidateIncomingEvent(SyncEventEnvelope syncEvent)
    {
        if (syncEvent.ProtocolVersion != SyncProtocol.CurrentVersion ||
            syncEvent.SpaceId == Guid.Empty ||
            syncEvent.EventId == Guid.Empty ||
            syncEvent.DeviceId == Guid.Empty ||
            (syncEvent.ChangeKind == SyncChangeKind.SetSetting
                ? syncEvent.ItemId != Guid.Empty
                : syncEvent.ItemId == Guid.Empty) ||
            syncEvent.Sequence <= 0 ||
            syncEvent.LogicalTimestamp <= 0 ||
            !Enum.IsDefined(syncEvent.ChangeKind))
        {
            throw new InvalidDataException("A remote sync event header is invalid.");
        }

        try
        {
            _ = DateTimeOffset.FromUnixTimeMilliseconds(syncEvent.CreatedAtUnixMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("A remote sync event time is invalid.", exception);
        }

        switch (syncEvent.ChangeKind)
        {
            case SyncChangeKind.Upsert or SyncChangeKind.Restore:
                if (syncEvent.Item is null || syncEvent.Tags is not null ||
                    syncEvent.IsPinned is not null || syncEvent.Setting is not null)
                {
                    throw new InvalidDataException("A remote content event shape is invalid.");
                }

                ValidateIncomingItem(syncEvent.Item);
                break;
            case SyncChangeKind.SetTags:
                if (syncEvent.Item is not null || syncEvent.Tags is null ||
                    syncEvent.IsPinned is not null || syncEvent.Setting is not null)
                {
                    throw new InvalidDataException("A remote tag event shape is invalid.");
                }

                IReadOnlyList<string> normalized = SyncConflictRules.NormalizeTags(syncEvent.Tags);
                if (!normalized.SequenceEqual(syncEvent.Tags, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("Remote tags are not canonical.");
                }

                break;
            case SyncChangeKind.SetPinned:
                if (syncEvent.Item is not null || syncEvent.Tags is not null ||
                    syncEvent.IsPinned is null || syncEvent.Setting is not null)
                {
                    throw new InvalidDataException("A remote pin event shape is invalid.");
                }

                break;
            case SyncChangeKind.Delete:
                if (syncEvent.Item is not null || syncEvent.Tags is not null ||
                    syncEvent.IsPinned is not null || syncEvent.Setting is not null)
                {
                    throw new InvalidDataException("A remote delete event shape is invalid.");
                }

                break;
            case SyncChangeKind.SetSetting:
                if (syncEvent.Item is not null || syncEvent.Tags is not null ||
                    syncEvent.IsPinned is not null || syncEvent.Setting is null ||
                    !SynchronizedSettingRegistry.IsValidValue(
                        syncEvent.Setting.Key,
                        syncEvent.Setting.Value))
                {
                    throw new InvalidDataException("A remote setting event shape is invalid.");
                }

                break;
            default:
                throw new InvalidDataException("A remote sync event kind is invalid.");
        }
    }

    private static void ValidateIncomingItem(SyncClipboardItemPayload item)
    {
        ValidateBlobHash(item.ContentHash, nameof(item.ContentHash));
        if (!Enum.IsDefined(item.PrimaryKind) ||
            item.DisplayCategory is < 1 or > 4 ||
            item.TotalSizeBytes is < 0 or > SyncProtocol.MaximumBlobPlaintextBytes ||
            item.PreviewText.Length > 65_536 ||
            item.SearchableText.Length > 1_048_576 ||
            item.SourceAttributionKind is < 0 or > 100 ||
            item.Representations is null ||
            item.Representations.Length > SyncProtocol.MaximumRepresentationsPerItem)
        {
            throw new InvalidDataException("A remote sync item is outside limits.");
        }

        _ = DateTimeOffset.FromUnixTimeMilliseconds(item.CapturedAtUnixMilliseconds);
        ValidateRemoteSourceIdentity(item.SourceApplication);
        ValidateRemoteSourceIdentity(item.SourceApplicationUserModelId);
        ValidateRemoteSourceIdentity(item.SourcePackageFamilyName);
        if (item.PrimaryKind == SyncPayloadKind.FileReference)
        {
            if (item.Representations.Length != 0 || item.Thumbnail is not null ||
                item.PreviewText != SyncProtocol.FileReferencePreview ||
                item.SearchableText.Length != 0 || item.TotalSizeBytes != 0)
            {
                throw new InvalidDataException("A file-reference payload exposed local metadata.");
            }

            return;
        }

        HashSet<SyncPayloadKind> kinds = [];
        foreach (SyncRepresentationPayload representation in item.Representations)
        {
            if (!Enum.IsDefined(representation.Kind) ||
                representation.Kind == SyncPayloadKind.FileReference ||
                !kinds.Add(representation.Kind) ||
                representation.SizeBytes is < 0 or > SyncProtocol.MaximumBlobPlaintextBytes ||
                representation.Width < 0 || representation.Height < 0 ||
                representation.BitsPerPixel is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException("A remote representation is invalid.");
            }

            ValidateMediaType(representation.MediaType);
            int populated = (representation.Text is null ? 0 : 1) +
                (representation.InlineData is null ? 0 : 1) +
                (representation.BlobHash is null ? 0 : 1);
            if (populated != 1)
            {
                throw new InvalidDataException("A remote representation storage form is invalid.");
            }

            if (representation.Text is not null &&
                System.Text.Encoding.UTF8.GetByteCount(representation.Text) !=
                representation.SizeBytes)
            {
                throw new InvalidDataException("A remote text representation size is invalid.");
            }

            if (representation.InlineData is not null &&
                representation.InlineData.LongLength != representation.SizeBytes)
            {
                throw new InvalidDataException("A remote inline representation size is invalid.");
            }

            if (representation.BlobHash is not null)
            {
                ValidateBlobHash(representation.BlobHash, nameof(representation.BlobHash));
            }

            if (representation.BitmapEncoding is < 1 or > 4)
            {
                throw new InvalidDataException("A remote bitmap encoding is invalid.");
            }
        }

        if (item.Thumbnail is not null)
        {
            ValidateBlobHash(item.Thumbnail.Hash, nameof(item.Thumbnail.Hash));
            ValidateMediaType(item.Thumbnail.MediaType);
            if (item.Thumbnail.SizeBytes is <= 0 or > SyncProtocol.MaximumBlobPlaintextBytes)
            {
                throw new InvalidDataException("A remote thumbnail size is invalid.");
            }
        }
    }

    private static void ValidateRemoteSourceIdentity(string? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > 1024 || value.Any(char.IsControl) ||
            value.Contains('/') || value.Contains('\\'))
        {
            throw new InvalidDataException("A remote source identity is invalid.");
        }
    }

    private static SyncPayloadKind ParsePayloadKind(int value) => value switch
    {
        >= (int)SyncPayloadKind.Text and <= (int)SyncPayloadKind.FileReference =>
            (SyncPayloadKind)value,
        _ => throw new InvalidDataException("A sync payload kind is invalid."),
    };

    private static void ZeroSyncPayload(SyncClipboardItemPayload? payload)
    {
        if (payload is null)
        {
            return;
        }

        foreach (SyncRepresentationPayload representation in payload.Representations)
        {
            if (representation.InlineData is not null)
            {
                CryptographicOperations.ZeroMemory(representation.InlineData);
            }
        }
    }

    private static void ValidateSyncIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static Guid ParseCanonicalGuid(string value)
    {
        if (!Guid.TryParseExact(value, "N", out Guid parsed) || parsed == Guid.Empty ||
            !string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A stored sync identifier is invalid.");
        }

        return parsed;
    }

    private static void ValidateBlobHash(string value, string parameterName)
    {
        if (!SyncRemoteLayout.IsLowerHex(value, 64))
        {
            throw new ArgumentException("A sync Blob hash is invalid.", parameterName);
        }
    }

    private static void ValidateMediaType(string mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        if (mediaType.Length > 256 || mediaType.Any(char.IsControl))
        {
            throw new ArgumentException("A sync media type is invalid.", nameof(mediaType));
        }
    }

    private static void ValidateEtag(string? etag)
    {
        if (etag is { Length: > 256 } || etag?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("A sync ETag is invalid.", nameof(etag));
        }
    }

    private sealed record LocalSyncIdentity(
        Guid SpaceId,
        Guid DeviceId,
        int KeyVersion);

    private sealed record RemoteApplyCoreResult(
        SyncEventApplyResult Result,
        IReadOnlyList<string> BlobPathsToDelete);
}
