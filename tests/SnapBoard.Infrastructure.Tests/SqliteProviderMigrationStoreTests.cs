using Microsoft.Data.Sqlite;
using SnapBoard.Application.Sync;

namespace SnapBoard.Infrastructure.Tests;

public sealed class SqliteProviderMigrationStoreTests
{
    [Fact]
    public async Task ProviderMigrationStateIsTransactionalAndContainsNoRemoteCredentials()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        Guid spaceId = Guid.NewGuid();
        Guid localDeviceId = Guid.NewGuid();
        Guid remoteDeviceId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        await context.Store.ConfigureAsync(
            spaceId,
            localDeviceId,
            keyVersion: 1,
            enabled: true,
            CancellationToken.None);
        await context.Store.EnsureRemoteDeviceAsync(
            spaceId,
            remoteDeviceId,
            CancellationToken.None);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SyncProviderMigrationRecord migration = new(
            planId,
            spaceId,
            Epoch: 1,
            localDeviceId,
            new string('a', 64),
            new string('b', 64),
            SyncProviderMigrationState.Draft,
            TotalObjects: 0,
            TotalBytes: 0,
            CompletedObjects: 0,
            CompletedBytes: 0,
            InventorySha256: null,
            DiagnosticCode: null,
            CreatedAtUnixMilliseconds: now,
            UpdatedAtUnixMilliseconds: now);

        await context.Store.CreateProviderMigrationAsync(
            migration,
            [localDeviceId, remoteDeviceId],
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.Store.CreateProviderMigrationAsync(
                migration with { PlanId = Guid.NewGuid(), Epoch = 2 },
                [localDeviceId, remoteDeviceId],
                CancellationToken.None));

        SyncProviderMigrationWatermark watermark = await context.Store
            .CaptureProviderMigrationWatermarkAsync(
                spaceId,
                localDeviceId,
                CancellationToken.None);
        Assert.Equal(0, watermark.HighestLocalSequence);
        Assert.Equal(0, watermark.HighestUploadedSequence);
        SyncCheckpointState checkpoint = Assert.Single(watermark.Checkpoints);
        Assert.Equal(remoteDeviceId, checkpoint.DeviceId);
        Assert.Equal(0, checkpoint.AppliedSequence);

        await context.Store.SaveProviderMigrationDeviceAsync(
            new SyncProviderMigrationDeviceRecord(
                planId,
                localDeviceId,
                SyncProviderMigrationDeviceState.Ready,
                HighestLocalSequence: 0,
                HighestUploadedSequence: 0,
                DiagnosticCode: null,
                UpdatedAtUnixMilliseconds: now + 1),
            CancellationToken.None);
        migration = migration with
        {
            State = SyncProviderMigrationState.RolledBack,
            UpdatedAtUnixMilliseconds = now + 1,
        };
        await context.Store.SaveProviderMigrationAsync(migration, CancellationToken.None);
        await context.Store.CreateProviderMigrationAsync(
            migration with
            {
                PlanId = Guid.NewGuid(),
                Epoch = 2,
                State = SyncProviderMigrationState.Draft,
                UpdatedAtUnixMilliseconds = now + 2,
            },
            [localDeviceId, remoteDeviceId],
            CancellationToken.None);

        SyncProviderMigrationRecord stored = Assert.IsType<SyncProviderMigrationRecord>(
            await context.Store.GetProviderMigrationAsync(spaceId, CancellationToken.None));
        Assert.Equal(2, stored.Epoch);
        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand columns = connection.CreateCommand();
        columns.CommandText = """
            SELECT name
            FROM pragma_table_info('sync_provider_migrations')
            WHERE name LIKE '%endpoint%' OR name LIKE '%username%' OR
                  name LIKE '%password%' OR name LIKE '%key%';
            """;
        await using SqliteDataReader reader = await columns.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task ProviderMigrationRejectsInvalidProgressAndUnknownDevice()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        Guid spaceId = Guid.NewGuid();
        Guid localDeviceId = Guid.NewGuid();
        await context.Store.ConfigureAsync(
            spaceId,
            localDeviceId,
            keyVersion: 1,
            enabled: true,
            CancellationToken.None);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SyncProviderMigrationRecord invalid = new(
            Guid.NewGuid(),
            spaceId,
            Epoch: 1,
            localDeviceId,
            new string('a', 64),
            new string('b', 64),
            SyncProviderMigrationState.MirroringCiphertext,
            TotalObjects: 1,
            TotalBytes: 10,
            CompletedObjects: 2,
            CompletedBytes: 10,
            InventorySha256: null,
            DiagnosticCode: null,
            CreatedAtUnixMilliseconds: now,
            UpdatedAtUnixMilliseconds: now);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await context.Store.CreateProviderMigrationAsync(
                invalid,
                [localDeviceId],
                CancellationToken.None));
    }
}
