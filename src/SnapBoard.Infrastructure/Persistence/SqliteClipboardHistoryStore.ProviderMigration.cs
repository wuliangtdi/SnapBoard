using Microsoft.Data.Sqlite;
using SnapBoard.Application.Sync;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Infrastructure.Persistence;

public sealed partial class SqliteClipboardHistoryStore
{
    public ValueTask<SyncProviderMigrationRecord?> GetProviderMigrationAsync(
        Guid spaceId,
        CancellationToken cancellationToken)
    {
        ValidateSyncIdentifier(spaceId, nameof(spaceId));
        return RunReadAsync(
            (connection, token) => ReadProviderMigrationCoreAsync(
                connection,
                spaceId,
                token),
            cancellationToken);
    }

    public ValueTask<IReadOnlyList<SyncProviderMigrationDeviceRecord>>
        GetProviderMigrationDevicesAsync(
            Guid planId,
            CancellationToken cancellationToken)
    {
        ValidateSyncIdentifier(planId, nameof(planId));
        return RunReadAsync(
            (connection, token) => ReadProviderMigrationDevicesCoreAsync(
                connection,
                planId,
                token),
            cancellationToken);
    }

    public async ValueTask CreateProviderMigrationAsync(
        SyncProviderMigrationRecord migration,
        IReadOnlyList<Guid> requiredDeviceIds,
        CancellationToken cancellationToken)
    {
        ValidateProviderMigration(migration);
        ArgumentNullException.ThrowIfNull(requiredDeviceIds);
        if (requiredDeviceIds.Count is < 1 or > SyncProviderMigrationProtocol.MaximumDevices ||
            requiredDeviceIds.Any(static deviceId => deviceId == Guid.Empty) ||
            requiredDeviceIds.Distinct().Count() != requiredDeviceIds.Count ||
            !requiredDeviceIds.Contains(migration.InitiatorDeviceId))
        {
            throw new ArgumentException(
                "The provider migration device set is invalid.",
                nameof(requiredDeviceIds));
        }

        Guid[] canonicalDevices = requiredDeviceIds
            .OrderBy(static deviceId => deviceId.ToString("N"), StringComparer.Ordinal)
            .ToArray();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                (connection, token) => CreateProviderMigrationCoreAsync(
                    connection,
                    migration,
                    canonicalDevices,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SaveProviderMigrationAsync(
        SyncProviderMigrationRecord migration,
        CancellationToken cancellationToken)
    {
        ValidateProviderMigration(migration);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                (connection, token) => SaveProviderMigrationCoreAsync(
                    connection,
                    migration,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask SaveProviderMigrationDeviceAsync(
        SyncProviderMigrationDeviceRecord device,
        CancellationToken cancellationToken)
    {
        ValidateProviderMigrationDevice(device);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                (connection, token) => SaveProviderMigrationDeviceCoreAsync(
                    connection,
                    device,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<SyncProviderMigrationWatermark> CaptureProviderMigrationWatermarkAsync(
        Guid spaceId,
        Guid localDeviceId,
        CancellationToken cancellationToken)
    {
        ValidateSyncIdentifier(spaceId, nameof(spaceId));
        ValidateSyncIdentifier(localDeviceId, nameof(localDeviceId));
        return RunReadAsync(
            (connection, token) => CaptureProviderMigrationWatermarkCoreAsync(
                connection,
                spaceId,
                localDeviceId,
                token),
            cancellationToken);
    }

    private static async ValueTask<SyncProviderMigrationRecord?>
        ReadProviderMigrationCoreAsync(
            SqliteConnection connection,
            Guid spaceId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT plan_id, space_id, epoch, initiator_device_id,
                   source_remote_fingerprint, target_remote_fingerprint, state,
                   total_objects, total_bytes, completed_objects, completed_bytes,
                   inventory_sha256, diagnostic_code, created_at_utc, updated_at_utc
            FROM sync_provider_migrations
            WHERE space_id = @spaceId
            ORDER BY epoch DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@spaceId", spaceId.ToString("N"));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProviderMigration(reader)
            : null;
    }

    private static async ValueTask<IReadOnlyList<SyncProviderMigrationDeviceRecord>>
        ReadProviderMigrationDevicesCoreAsync(
            SqliteConnection connection,
            Guid planId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT plan_id, device_id, state, highest_local_sequence,
                   highest_uploaded_sequence, diagnostic_code, updated_at_utc
            FROM sync_provider_migration_devices
            WHERE plan_id = @planId
            ORDER BY device_id;
            """;
        command.Parameters.AddWithValue("@planId", planId.ToString("N"));
        List<SyncProviderMigrationDeviceRecord> devices = [];
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            SyncProviderMigrationDeviceRecord device = new(
                ParseCanonicalGuid(reader.GetString(0)),
                ParseCanonicalGuid(reader.GetString(1)),
                ParseProviderMigrationDeviceState(reader.GetInt32(2)),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt64(6));
            ValidateProviderMigrationDevice(device);
            devices.Add(device);
        }

        return devices;
    }

    private static async ValueTask<bool> CreateProviderMigrationCoreAsync(
        SqliteConnection connection,
        SyncProviderMigrationRecord migration,
        IReadOnlyList<Guid> requiredDeviceIds,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        try
        {
            await using (SqliteCommand active = connection.CreateCommand())
            {
                active.Transaction = transaction;
                active.CommandText = """
                    SELECT COUNT(*)
                    FROM sync_provider_migrations
                    WHERE space_id = @spaceId AND state NOT IN (11, 13, 14);
                    """;
                active.Parameters.AddWithValue("@spaceId", migration.SpaceId.ToString("N"));
                long activeCount = (long)(await active.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false) ?? 0L);
                if (activeCount != 0)
                {
                    throw new InvalidOperationException(
                        "The sync space already has an active provider migration.");
                }
            }

            await using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO sync_provider_migrations(
                        plan_id, space_id, epoch, initiator_device_id,
                        source_remote_fingerprint, target_remote_fingerprint, state,
                        total_objects, total_bytes, completed_objects, completed_bytes,
                        inventory_sha256, diagnostic_code, created_at_utc, updated_at_utc)
                    VALUES (
                        @planId, @spaceId, @epoch, @initiatorDeviceId,
                        @sourceFingerprint, @targetFingerprint, @state,
                        @totalObjects, @totalBytes, @completedObjects, @completedBytes,
                        @inventorySha256, @diagnosticCode, @createdAt, @updatedAt);
                    """;
                AddProviderMigrationParameters(insert, migration);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (Guid deviceId in requiredDeviceIds)
            {
                await using SqliteCommand insertDevice = connection.CreateCommand();
                insertDevice.Transaction = transaction;
                insertDevice.CommandText = """
                    INSERT INTO sync_provider_migration_devices(
                        plan_id, device_id, state, highest_local_sequence,
                        highest_uploaded_sequence, updated_at_utc)
                    VALUES (@planId, @deviceId, 0, 0, 0, @updatedAt);
                    """;
                insertDevice.Parameters.AddWithValue("@planId", migration.PlanId.ToString("N"));
                insertDevice.Parameters.AddWithValue("@deviceId", deviceId.ToString("N"));
                insertDevice.Parameters.AddWithValue("@updatedAt", migration.UpdatedAtUnixMilliseconds);
                await insertDevice.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<bool> SaveProviderMigrationCoreAsync(
        SqliteConnection connection,
        SyncProviderMigrationRecord migration,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_provider_migrations
            SET state = @state,
                total_objects = @totalObjects,
                total_bytes = @totalBytes,
                completed_objects = @completedObjects,
                completed_bytes = @completedBytes,
                inventory_sha256 = @inventorySha256,
                diagnostic_code = @diagnosticCode,
                updated_at_utc = @updatedAt
            WHERE plan_id = @planId AND space_id = @spaceId AND epoch = @epoch AND
                  initiator_device_id = @initiatorDeviceId AND
                  source_remote_fingerprint = @sourceFingerprint AND
                  target_remote_fingerprint = @targetFingerprint AND
                  created_at_utc = @createdAt;
            """;
        AddProviderMigrationParameters(command, migration);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The provider migration is unavailable.");
        }

        return true;
    }

    private static async ValueTask<bool> SaveProviderMigrationDeviceCoreAsync(
        SqliteConnection connection,
        SyncProviderMigrationDeviceRecord device,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_provider_migration_devices
            SET state = @state,
                highest_local_sequence = @highestLocalSequence,
                highest_uploaded_sequence = @highestUploadedSequence,
                diagnostic_code = @diagnosticCode,
                updated_at_utc = @updatedAt
            WHERE plan_id = @planId AND device_id = @deviceId;
            """;
        command.Parameters.AddWithValue("@planId", device.PlanId.ToString("N"));
        command.Parameters.AddWithValue("@deviceId", device.DeviceId.ToString("N"));
        command.Parameters.AddWithValue("@state", (int)device.State);
        command.Parameters.AddWithValue("@highestLocalSequence", device.HighestLocalSequence);
        command.Parameters.AddWithValue("@highestUploadedSequence", device.HighestUploadedSequence);
        command.Parameters.AddWithValue(
            "@diagnosticCode",
            (object?)device.DiagnosticCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@updatedAt", device.UpdatedAtUnixMilliseconds);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The provider migration device is unavailable.");
        }

        return true;
    }

    private static async ValueTask<SyncProviderMigrationWatermark>
        CaptureProviderMigrationWatermarkCoreAsync(
            SqliteConnection connection,
            Guid spaceId,
            Guid localDeviceId,
            CancellationToken cancellationToken)
    {
        long highestLocalSequence;
        await using (SqliteCommand local = connection.CreateCommand())
        {
            local.CommandText = """
                SELECT next_sequence - 1
                FROM sync_devices
                WHERE space_id = @spaceId AND device_id = @deviceId AND
                      is_local = 1 AND revoked_at_utc IS NULL;
                """;
            local.Parameters.AddWithValue("@spaceId", spaceId.ToString("N"));
            local.Parameters.AddWithValue("@deviceId", localDeviceId.ToString("N"));
            object? value = await local.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not long sequence)
            {
                throw new InvalidOperationException("The local sync device is unavailable.");
            }

            highestLocalSequence = sequence;
        }

        long highestUploadedSequence;
        await using (SqliteCommand uploaded = connection.CreateCommand())
        {
            uploaded.CommandText = """
                SELECT COALESCE(MAX(sequence), 0)
                FROM sync_outbox
                WHERE space_id = @spaceId AND device_id = @deviceId AND state = 2;
                """;
            uploaded.Parameters.AddWithValue("@spaceId", spaceId.ToString("N"));
            uploaded.Parameters.AddWithValue("@deviceId", localDeviceId.ToString("N"));
            highestUploadedSequence = (long)(await uploaded
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0L);
        }

        List<SyncCheckpointState> checkpoints = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT remote_device_id, applied_sequence, applied_event_id, remote_etag
                FROM sync_checkpoints
                WHERE space_id = @spaceId
                ORDER BY remote_device_id;
                """;
            command.Parameters.AddWithValue("@spaceId", spaceId.ToString("N"));
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                checkpoints.Add(new SyncCheckpointState(
                    spaceId,
                    ParseCanonicalGuid(reader.GetString(0)),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : ParseCanonicalGuid(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return new SyncProviderMigrationWatermark(
            highestLocalSequence,
            highestUploadedSequence,
            checkpoints);
    }

    private static SyncProviderMigrationRecord ReadProviderMigration(SqliteDataReader reader)
    {
        SyncProviderMigrationRecord migration = new(
            ParseCanonicalGuid(reader.GetString(0)),
            ParseCanonicalGuid(reader.GetString(1)),
            reader.GetInt64(2),
            ParseCanonicalGuid(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            ParseProviderMigrationState(reader.GetInt32(6)),
            reader.GetInt32(7),
            reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetInt64(13),
            reader.GetInt64(14));
        ValidateProviderMigration(migration);
        return migration;
    }

    private static void AddProviderMigrationParameters(
        SqliteCommand command,
        SyncProviderMigrationRecord migration)
    {
        command.Parameters.AddWithValue("@planId", migration.PlanId.ToString("N"));
        command.Parameters.AddWithValue("@spaceId", migration.SpaceId.ToString("N"));
        command.Parameters.AddWithValue("@epoch", migration.Epoch);
        command.Parameters.AddWithValue(
            "@initiatorDeviceId",
            migration.InitiatorDeviceId.ToString("N"));
        command.Parameters.AddWithValue("@sourceFingerprint", migration.SourceRemoteFingerprint);
        command.Parameters.AddWithValue("@targetFingerprint", migration.TargetRemoteFingerprint);
        command.Parameters.AddWithValue("@state", (int)migration.State);
        command.Parameters.AddWithValue("@totalObjects", migration.TotalObjects);
        command.Parameters.AddWithValue("@totalBytes", migration.TotalBytes);
        command.Parameters.AddWithValue("@completedObjects", migration.CompletedObjects);
        command.Parameters.AddWithValue("@completedBytes", migration.CompletedBytes);
        command.Parameters.AddWithValue(
            "@inventorySha256",
            (object?)migration.InventorySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@diagnosticCode",
            (object?)migration.DiagnosticCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdAt", migration.CreatedAtUnixMilliseconds);
        command.Parameters.AddWithValue("@updatedAt", migration.UpdatedAtUnixMilliseconds);
    }

    private static void ValidateProviderMigration(SyncProviderMigrationRecord migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ValidateSyncIdentifier(migration.PlanId, nameof(migration));
        ValidateSyncIdentifier(migration.SpaceId, nameof(migration));
        ValidateSyncIdentifier(migration.InitiatorDeviceId, nameof(migration));
        if (migration.Epoch <= 0 || migration.State == SyncProviderMigrationState.None ||
            !Enum.IsDefined(migration.State) || migration.TotalObjects < 0 ||
            migration.TotalBytes < 0 || migration.CompletedObjects < 0 ||
            migration.CompletedObjects > migration.TotalObjects || migration.CompletedBytes < 0 ||
            migration.CompletedBytes > migration.TotalBytes ||
            migration.CreatedAtUnixMilliseconds <= 0 ||
            migration.UpdatedAtUnixMilliseconds < migration.CreatedAtUnixMilliseconds ||
            !SyncRemoteLayout.IsLowerHex(migration.SourceRemoteFingerprint, 64) ||
            !SyncRemoteLayout.IsLowerHex(migration.TargetRemoteFingerprint, 64) ||
            migration.InventorySha256 is not null &&
                !SyncRemoteLayout.IsLowerHex(migration.InventorySha256, 64))
        {
            throw new ArgumentException("The provider migration record is invalid.", nameof(migration));
        }

        ValidateDiagnosticCode(migration.DiagnosticCode);
    }

    private static void ValidateProviderMigrationDevice(
        SyncProviderMigrationDeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        ValidateSyncIdentifier(device.PlanId, nameof(device));
        ValidateSyncIdentifier(device.DeviceId, nameof(device));
        if (!Enum.IsDefined(device.State) || device.HighestLocalSequence < 0 ||
            device.HighestUploadedSequence < 0 ||
            device.HighestUploadedSequence > device.HighestLocalSequence ||
            device.UpdatedAtUnixMilliseconds <= 0)
        {
            throw new ArgumentException(
                "The provider migration device record is invalid.",
                nameof(device));
        }

        ValidateDiagnosticCode(device.DiagnosticCode);
    }

    private static void ValidateDiagnosticCode(string? diagnosticCode)
    {
        if (diagnosticCode is { Length: < 1 or > 128 } ||
            diagnosticCode?.Any(static character => char.IsControl(character)) == true)
        {
            throw new ArgumentException("A provider migration diagnostic code is invalid.");
        }
    }

    private static SyncProviderMigrationState ParseProviderMigrationState(int value) =>
        value is >= (int)SyncProviderMigrationState.Draft and
            <= (int)SyncProviderMigrationState.Failed
            ? (SyncProviderMigrationState)value
            : throw new InvalidDataException("A provider migration state is invalid.");

    private static SyncProviderMigrationDeviceState ParseProviderMigrationDeviceState(int value) =>
        value is >= (int)SyncProviderMigrationDeviceState.Pending and
            <= (int)SyncProviderMigrationDeviceState.Failed
            ? (SyncProviderMigrationDeviceState)value
            : throw new InvalidDataException("A provider migration device state is invalid.");
}
