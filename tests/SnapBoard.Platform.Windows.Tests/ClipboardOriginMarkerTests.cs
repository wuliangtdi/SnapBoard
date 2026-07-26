using SnapBoard.Platform.Windows.Clipboard;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class ClipboardOriginMarkerTests
{
    [Fact]
    public void MarkerMatchesOnlyCurrentAdapterPayload()
    {
        ClipboardOriginMarker first = new();
        ClipboardOriginMarker second = new();
        byte[] terminatedPayload = [.. first.Payload.ToArray(), 0];

        Assert.True(first.Matches(terminatedPayload));
        Assert.False(second.Matches(terminatedPayload));
    }
}
