using System.Text.Json;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Sync.WebDav.Tests;

public sealed class SyncJsonContextTests
{
    [Fact]
    public void SourceGeneratedContextRoundTripsEnvelope()
    {
        SyncEventEnvelope expected = new(
            SyncProtocol.CurrentVersion,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            42,
            51,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SyncChangeKind.SetPinned,
            Guid.CreateVersion7(),
            Item: null,
            Tags: null,
            IsPinned: true);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            expected,
            SyncJsonContext.Default.SyncEventEnvelope);
        SyncEventEnvelope? actual = JsonSerializer.Deserialize(
            json,
            SyncJsonContext.Default.SyncEventEnvelope);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SourceGeneratedContextRoundTripsSourceApplicationIconDescriptor()
    {
        SyncClipboardItemPayload expected = new(
            new string('a', 64),
            SyncPayloadKind.Text,
            DisplayCategory: 1,
            CapturedAtUnixMilliseconds: 123456789,
            PreviewText: "source icon",
            SearchableText: "source icon",
            SourceApplication: "source-app",
            SourceApplicationUserModelId: null,
            SourcePackageFamilyName: null,
            SourceAttributionKind: 0,
            Representations: [],
            Thumbnail: null,
            TotalSizeBytes: 11,
            SourceApplicationIcon: new SyncSourceApplicationIconPayload(
                new SyncBlobReferencePayload(
                    new string('b', 64),
                    SyncProtocol.SourceApplicationIconMediaType,
                    SyncProtocol.SourceApplicationIconSizeBytes),
                SyncProtocol.SourceApplicationIconFormatVersion,
                SyncProtocol.SourceApplicationIconWidth,
                SyncProtocol.SourceApplicationIconHeight,
                SyncProtocol.SourceApplicationIconStride));

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            expected,
            SyncJsonContext.Default.SyncClipboardItemPayload);
        SyncClipboardItemPayload actual = Assert.IsType<SyncClipboardItemPayload>(
            JsonSerializer.Deserialize(
                json,
                SyncJsonContext.Default.SyncClipboardItemPayload));

        Assert.Equal(expected.SourceApplicationIcon, actual.SourceApplicationIcon);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(1, SyncProtocol.CurrentVersion);
        Assert.Equal("v1", SyncProtocol.VersionDirectoryName);
    }

    [Fact]
    public void SourceGeneratedContextRoundTripsSynchronizedSetting()
    {
        SyncEventEnvelope expected = new(
            SyncProtocol.CurrentVersion,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            7,
            9,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SyncChangeKind.SetSetting,
            Guid.Empty,
            Item: null,
            Tags: null,
            IsPinned: null,
            Setting: new SyncSettingPayload(
                "history.capture",
                "{\"text\":true,\"richText\":true,\"images\":false,\"files\":true}"));

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            expected,
            SyncJsonContext.Default.SyncEventEnvelope);
        SyncEventEnvelope? actual = JsonSerializer.Deserialize(
            json,
            SyncJsonContext.Default.SyncEventEnvelope);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SourceGeneratedContextRoundTripsProviderMigrationIntent()
    {
        Guid firstDeviceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid secondDeviceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        SyncProviderMigrationIntent expected = new(
            SyncProviderMigrationProtocol.CurrentVersion,
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Epoch: 7,
            firstDeviceId,
            "https://source.example.test/dav/",
            "SourceRoot",
            new string('a', 64),
            SourceAllowInsecureLoopback: false,
            new string('b', 64),
            "https://target.example.test/dav/",
            "TargetRoot",
            new string('c', 64),
            TargetAllowInsecureLoopback: false,
            new string('d', 64),
            [firstDeviceId, secondDeviceId],
            CreatedAtUnixMilliseconds: 123456789);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            expected,
            SyncJsonContext.Default.SyncProviderMigrationIntent);
        SyncProviderMigrationIntent actual = Assert.IsType<SyncProviderMigrationIntent>(
            JsonSerializer.Deserialize(
                json,
                SyncJsonContext.Default.SyncProviderMigrationIntent));

        Assert.Equal(expected with { RequiredDeviceIds = actual.RequiredDeviceIds }, actual);
        Assert.Equal(expected.RequiredDeviceIds, actual.RequiredDeviceIds);
    }

    [Fact]
    public void RemoteLayoutUsesOnlyCanonicalProtocolIdentifiers()
    {
        Guid spaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid deviceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid eventId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        string path = SyncRemoteLayout.GetEventPath(spaceId, deviceId, 42, eventId);

        Assert.Equal(
            "SnapBoard/v1/spaces/11111111111111111111111111111111/devices/22222222222222222222222222222222/events/00000000000000000042-33333333333333333333333333333333.enc",
            path);
        Assert.True(SyncRemoteLayout.TryParseEventObjectName(
            "00000000000000000042-33333333333333333333333333333333.enc",
            out long sequence,
            out Guid parsedEventId));
        Assert.Equal(42, sequence);
        Assert.Equal(eventId, parsedEventId);
        Assert.False(SyncRemoteLayout.TryParseEventObjectName(
            "../../00000000000000000042-33333333333333333333333333333333.enc",
            out _,
            out _));

        Guid planId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Assert.Equal(
            "SnapBoard/v1/spaces/11111111111111111111111111111111/migrations/" +
            "44444444444444444444444444444444/ready/" +
            "22222222222222222222222222222222.enc",
            SyncRemoteLayout.GetProviderMigrationDeviceMarkerPath(
                spaceId,
                planId,
                SyncProviderMigrationMarkerKind.Ready,
                deviceId));
        Assert.Equal(
            "SnapBoard/v1/spaces/11111111111111111111111111111111/migrations/" +
            "44444444444444444444444444444444/terminal.enc",
            SyncRemoteLayout.GetProviderMigrationDecisionPath(
                spaceId,
                planId,
                SyncProviderMigrationMarkerKind.Terminal));
    }
}
