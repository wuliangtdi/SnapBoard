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
    }
}
