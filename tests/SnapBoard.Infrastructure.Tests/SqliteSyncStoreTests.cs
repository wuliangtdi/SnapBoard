using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Sync;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Infrastructure.Tests;

public sealed class SqliteSyncStoreTests
{
    [Fact]
    public async Task LocalMutationsAppendOrderedOutboxAndScrubUploadedPlaintext()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        Guid spaceId = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        await context.Store.ConfigureAsync(
            spaceId,
            deviceId,
            keyVersion: 1,
            enabled: true,
            CancellationToken.None);
        ClipboardCapturedItem item = CreateTextItem("local sync", @"C:\private\source.exe");

        await context.Store.SaveAsync(item, CancellationToken.None);
        Assert.True(await context.Store.SetPinnedAsync(
            item.Id,
            true,
            CancellationToken.None));
        Assert.True(await context.Store.SetTagsAsync(
            item.Id,
            ["Beta", "alpha"],
            CancellationToken.None));
        Assert.True(await context.Store.SoftDeleteAsync(item.Id, CancellationToken.None));

        IReadOnlyList<SyncOutboxItem> outbox = await context.Store.ReadOutboxBatchAsync(
            10,
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        try
        {
            Assert.Equal(4, outbox.Count);
            Assert.Equal(
                [
                    SyncChangeKind.Upsert,
                    SyncChangeKind.SetPinned,
                    SyncChangeKind.SetTags,
                    SyncChangeKind.Delete,
                ],
                outbox.Select(entry => entry.Event.ChangeKind));
            Assert.Equal([1L, 2L, 3L, 4L], outbox.Select(entry => entry.Event.Sequence));
            Assert.Equal([1L, 2L, 3L, 4L], outbox.Select(entry => entry.Event.LogicalTimestamp));
            Assert.All(outbox, entry =>
            {
                Assert.Equal(spaceId, entry.Event.SpaceId);
                Assert.Equal(deviceId, entry.Event.DeviceId);
                Assert.Equal(item.Id.Value, entry.Event.ItemId);
                Assert.Equal(-1, entry.SerializedEvent.AsSpan().IndexOf("private"u8));
            });
            Assert.Equal(["alpha", "Beta"], outbox[2].Event.Tags!);

            DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddHours(1);
            await context.Store.MarkOutboxFailedAsync(
                outbox[0].Event.EventId,
                SyncPersistenceErrorCategory.Network,
                retryAt,
                CancellationToken.None);
            IReadOnlyList<SyncOutboxItem> beforeRetry = await context.Store.ReadOutboxBatchAsync(
                10,
                retryAt.AddMilliseconds(-1),
                CancellationToken.None);
            try
            {
                Assert.DoesNotContain(
                    beforeRetry,
                    entry => entry.Event.EventId == outbox[0].Event.EventId);
            }
            finally
            {
                ZeroOutbox(beforeRetry);
            }

            IReadOnlyList<SyncOutboxItem> afterRetry = await context.Store.ReadOutboxBatchAsync(
                10,
                retryAt,
                CancellationToken.None);
            try
            {
                Assert.Equal(
                    1,
                    Assert.Single(afterRetry, entry =>
                        entry.Event.EventId == outbox[0].Event.EventId).RetryCount);
            }
            finally
            {
                ZeroOutbox(afterRetry);
            }

            await context.Store.MarkOutboxUploadedAsync(
                outbox[0].Event.EventId,
                "\"event-etag\"",
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            await using SqliteConnection connection = await context.ConnectionFactory
                .OpenConnectionAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT state, length(event_json)
                FROM sync_outbox
                WHERE event_id = @eventId;
                """;
            command.Parameters.AddWithValue("@eventId", outbox[0].Event.EventId.ToString("N"));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
        }
        finally
        {
            ZeroOutbox(outbox);
        }
    }

    [Fact]
    public async Task SynchronizedSettingUsesOrderedOutboxAndPerKeyLastWriterWins()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        Guid spaceId = Guid.NewGuid();
        Guid localDeviceId = Guid.NewGuid();
        Guid remoteDeviceId = Guid.NewGuid();
        await ConfigureRemoteAsync(context, spaceId, localDeviceId, remoteDeviceId);
        const string localValue =
            "{\"text\":true,\"richText\":true,\"images\":false,\"files\":true}";
        const string remoteValue =
            "{\"text\":false,\"richText\":true,\"images\":true,\"files\":false}";

        await context.Store.SetSettingAsync(
            HistorySettingKeys.Capture,
            localValue,
            CancellationToken.None);
        IReadOnlyList<SyncOutboxItem> outbox = await context.Store.ReadOutboxBatchAsync(
            10,
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        try
        {
            SyncOutboxItem local = Assert.Single(outbox);
            Assert.Equal(SyncChangeKind.SetSetting, local.Event.ChangeKind);
            Assert.Equal(Guid.Empty, local.Event.ItemId);
            Assert.Equal(1, local.Event.Sequence);
            Assert.Equal(HistorySettingKeys.Capture, local.Event.Setting?.Key);
            Assert.Equal(localValue, local.Event.Setting?.Value);
        }
        finally
        {
            ZeroOutbox(outbox);
        }

        SyncEventEnvelope remote = CreateRemoteSetting(
            spaceId,
            remoteDeviceId,
            sequence: 1,
            logicalTimestamp: 10,
            key: HistorySettingKeys.Capture,
            value: remoteValue);
        byte[] serializedRemote = Serialize(remote);
        try
        {
            SyncEventApplyResult applied = await context.Store.ApplyRemoteEventAsync(
                remote,
                serializedRemote,
                "\"setting-1\"",
                CancellationToken.None);
            Assert.Equal(SyncEventApplyStatus.Applied, applied.Status);
            Assert.Equal(
                remoteValue,
                await context.Store.GetSettingAsync(
                    HistorySettingKeys.Capture,
                    CancellationToken.None));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serializedRemote);
        }

        SyncEventEnvelope stale = CreateRemoteSetting(
            spaceId,
            remoteDeviceId,
            sequence: 2,
            logicalTimestamp: 2,
            key: HistorySettingKeys.Capture,
            value: localValue);
        byte[] serializedStale = Serialize(stale);
        try
        {
            SyncEventApplyResult ignored = await context.Store.ApplyRemoteEventAsync(
                stale,
                serializedStale,
                "\"setting-2\"",
                CancellationToken.None);
            Assert.Equal(SyncEventApplyStatus.ConflictIgnored, ignored.Status);
            Assert.Equal(
                remoteValue,
                await context.Store.GetSettingAsync(
                    HistorySettingKeys.Capture,
                    CancellationToken.None));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serializedStale);
        }
    }

    [Fact]
    public async Task OutboxFailureRollsBackClipboardMutationAndSequence()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        Guid spaceId = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        await context.Store.ConfigureAsync(
            spaceId,
            deviceId,
            keyVersion: 1,
            enabled: true,
            CancellationToken.None);
        await using (SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_sync_outbox_insert
                BEFORE INSERT ON sync_outbox
                BEGIN
                    SELECT RAISE(ABORT, 'outbox-failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await context.Store.SaveAsync(
                CreateTextItem("must roll back"),
                CancellationToken.None));

        ClipboardHistoryPage history = await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None);
        Assert.Empty(history.Items);
        Assert.Empty(await context.Store.ReadOutboxBatchAsync(
            10,
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None));
        SyncConfigurationSnapshot configuration = Assert.IsType<SyncConfigurationSnapshot>(
            await context.Store.GetConfigurationAsync(CancellationToken.None));
        Assert.Equal(1, configuration.NextSequence);
    }

    [Fact]
    public async Task RemoteSequenceGapDoesNotAdvanceCheckpointAndExactReplayIsIdempotent()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        Guid spaceId = Guid.NewGuid();
        Guid localDeviceId = Guid.NewGuid();
        Guid remoteDeviceId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        await ConfigureRemoteAsync(context, spaceId, localDeviceId, remoteDeviceId);
        SyncEventEnvelope future = CreateRemoteUpsert(
            spaceId,
            remoteDeviceId,
            itemId,
            sequence: 2,
            logicalTimestamp: 2,
            "future");
        byte[] serializedFuture = Serialize(future);
        try
        {
            SyncEventApplyResult gap = await context.Store.ApplyRemoteEventAsync(
                future,
                serializedFuture,
                "\"future\"",
                CancellationToken.None);
            Assert.Equal(SyncEventApplyStatus.SequenceGap, gap.Status);
            Assert.Equal(1, gap.ExpectedSequence);
            Assert.Equal(
                0,
                (await context.Store.GetCheckpointAsync(
                    spaceId,
                    remoteDeviceId,
                    CancellationToken.None)).AppliedSequence);

            SyncEventEnvelope first = CreateRemoteUpsert(
                spaceId,
                remoteDeviceId,
                itemId,
                sequence: 1,
                logicalTimestamp: 1,
                "first");
            byte[] serializedFirst = Serialize(first);
            try
            {
                SyncEventApplyResult applied = await context.Store.ApplyRemoteEventAsync(
                    first,
                    serializedFirst,
                    "\"first\"",
                    CancellationToken.None);
                Assert.Equal(SyncEventApplyStatus.Applied, applied.Status);
                Assert.Equal(2, applied.ExpectedSequence);

                SyncEventApplyResult duplicate = await context.Store.ApplyRemoteEventAsync(
                    first,
                    serializedFirst,
                    "\"first-replay\"",
                    CancellationToken.None);
                Assert.Equal(SyncEventApplyStatus.Duplicate, duplicate.Status);
                Assert.Equal(2, duplicate.ExpectedSequence);

                SyncEventEnvelope alteredReplay = first with { EventId = Guid.NewGuid() };
                byte[] serializedAlteredReplay = Serialize(alteredReplay);
                try
                {
                    await Assert.ThrowsAsync<InvalidDataException>(async () =>
                        await context.Store.ApplyRemoteEventAsync(
                            alteredReplay,
                            serializedAlteredReplay,
                            null,
                            CancellationToken.None));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(serializedAlteredReplay);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serializedFirst);
            }

            SyncCheckpointState checkpoint = await context.Store.GetCheckpointAsync(
                spaceId,
                remoteDeviceId,
                CancellationToken.None);
            Assert.Equal(1, checkpoint.AppliedSequence);
            ClipboardHistoryItemSummary summary = Assert.Single((await context.Store.SearchAsync(
                new ClipboardHistoryQuery { PageSize = 10 },
                CancellationToken.None)).Items);
            Assert.Equal("first", summary.PreviewText);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serializedFuture);
        }
    }

    [Fact]
    public async Task VerifiedStagedBlobIsConsumedByRemoteUpsert()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        Guid spaceId = Guid.NewGuid();
        Guid localDeviceId = Guid.NewGuid();
        Guid remoteDeviceId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        await ConfigureRemoteAsync(context, spaceId, localDeviceId, remoteDeviceId);
        byte[] content = Enumerable.Range(0, 70 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();
        string blobHash = Hash(content);
        const string mediaType = "text/html";
        await context.Store.StageDownloadedBlobAsync(
            blobHash,
            mediaType,
            content,
            CancellationToken.None);
        SyncEventEnvelope syncEvent = CreateRemoteBlobUpsert(
            spaceId,
            remoteDeviceId,
            itemId,
            blobHash,
            mediaType,
            content.LongLength);
        byte[] serialized = Serialize(syncEvent);
        try
        {
            SyncEventApplyResult applied = await context.Store.ApplyRemoteEventAsync(
                syncEvent,
                serialized,
                "\"blob-event\"",
                CancellationToken.None);
            Assert.Equal(SyncEventApplyStatus.Applied, applied.Status);

            ClipboardHistoryContent historyContent = Assert.IsType<ClipboardHistoryContent>(
                await context.Store.GetContentAsync(
                    new ClipboardItemId(itemId),
                    CancellationToken.None));
            Assert.Equal(content, historyContent.Html.ToArray());
            using SyncBlobLease lease = Assert.IsType<SyncBlobLease>(
                await context.Store.OpenBlobAsync(blobHash, CancellationToken.None));
            Assert.Equal(mediaType, lease.MediaType);
            Assert.Equal(content, lease.Content.ToArray());

            await using SqliteConnection connection = await context.ConnectionFactory
                .OpenConnectionAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sync_blob_staging;";
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await command.ExecuteScalarAsync(),
                    CultureInfo.InvariantCulture));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task ConfigureRemoteAsync(
        HistoryStoreTestContext context,
        Guid spaceId,
        Guid localDeviceId,
        Guid remoteDeviceId)
    {
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
    }

    private static SyncEventEnvelope CreateRemoteUpsert(
        Guid spaceId,
        Guid deviceId,
        Guid itemId,
        long sequence,
        long logicalTimestamp,
        string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return new SyncEventEnvelope(
            SyncProtocol.CurrentVersion,
            spaceId,
            Guid.NewGuid(),
            deviceId,
            sequence,
            logicalTimestamp,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SyncChangeKind.Upsert,
            itemId,
            new SyncClipboardItemPayload(
                Hash(utf8),
                SyncPayloadKind.Text,
                (int)ClipboardHistoryDisplayCategory.Text,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                text,
                text,
                "remote-app",
                null,
                null,
                0,
                [
                    new SyncRepresentationPayload(
                        SyncPayloadKind.Text,
                        "text/plain; charset=utf-8",
                        text,
                        null,
                        null,
                        utf8.LongLength,
                        null,
                        0,
                        0,
                        0),
                ],
                null,
                utf8.LongLength),
            null,
            null);
    }

    private static SyncEventEnvelope CreateRemoteSetting(
        Guid spaceId,
        Guid deviceId,
        long sequence,
        long logicalTimestamp,
        string key,
        string value) => new(
        SyncProtocol.CurrentVersion,
        spaceId,
        Guid.NewGuid(),
        deviceId,
        sequence,
        logicalTimestamp,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SyncChangeKind.SetSetting,
        Guid.Empty,
        null,
        null,
        null,
        new SyncSettingPayload(key, value));

    private static SyncEventEnvelope CreateRemoteBlobUpsert(
        Guid spaceId,
        Guid deviceId,
        Guid itemId,
        string blobHash,
        string mediaType,
        long sizeBytes) => new(
        SyncProtocol.CurrentVersion,
        spaceId,
        Guid.NewGuid(),
        deviceId,
        1,
        1,
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SyncChangeKind.Upsert,
        itemId,
        new SyncClipboardItemPayload(
            Hash(Encoding.UTF8.GetBytes("remote-blob-item")),
            SyncPayloadKind.Html,
            (int)ClipboardHistoryDisplayCategory.Text,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "remote html",
            "remote html",
            "remote-app",
            null,
            null,
            0,
            [
                new SyncRepresentationPayload(
                    SyncPayloadKind.Html,
                    mediaType,
                    null,
                    null,
                    blobHash,
                    sizeBytes,
                    null,
                    0,
                    0,
                    0),
            ],
            null,
            sizeBytes),
        null,
        null);

    private static ClipboardCapturedItem CreateTextItem(
        string text,
        string? sourceExecutablePath = null)
    {
        ClipboardItemId id = ClipboardItemId.New();
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = DateTimeOffset.UtcNow,
            SourceProcessName = "test-source",
            SourceExecutablePath = sourceExecutablePath,
            ContentHash = new ClipboardContentHash(Hash(utf8)),
            PrimaryKind = ClipboardContentKind.Text,
            DisplayCategory = ClipboardHistoryDisplayCategory.Text,
            PreviewText = text,
            SearchableText = text,
            Representations =
            [
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Text,
                    "text/plain; charset=utf-8",
                    text,
                    default),
            ],
            Formats = [new ClipboardCapturedFormat("text", "Text", true)],
            TotalSizeBytes = utf8.LongLength,
        };
    }

    private static byte[] Serialize(SyncEventEnvelope syncEvent) =>
        JsonSerializer.SerializeToUtf8Bytes(
            syncEvent,
            SyncJsonContext.Default.SyncEventEnvelope);

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static void ZeroOutbox(IEnumerable<SyncOutboxItem> outbox)
    {
        foreach (SyncOutboxItem entry in outbox)
        {
            CryptographicOperations.ZeroMemory(entry.SerializedEvent);
        }
    }
}
