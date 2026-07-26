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
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            42,
            DateTimeOffset.UtcNow,
            SyncPayloadKind.Text,
            "events/00000042.segment.enc");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            expected,
            SyncJsonContext.Default.SyncEventEnvelope);
        SyncEventEnvelope? actual = JsonSerializer.Deserialize(
            json,
            SyncJsonContext.Default.SyncEventEnvelope);

        Assert.Equal(expected, actual);
    }
}
