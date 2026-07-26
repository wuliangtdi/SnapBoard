using SnapBoard.Platform.Windows.Clipboard;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class ClipboardSequenceDeduplicatorTests
{
    [Fact]
    public void RejectsZeroAndAdjacentDuplicatesButAllowsWraparound()
    {
        ClipboardSequenceDeduplicator deduplicator = new();

        Assert.False(deduplicator.TryAccept(0));
        Assert.True(deduplicator.TryAccept(uint.MaxValue));
        Assert.False(deduplicator.TryAccept(uint.MaxValue));
        Assert.True(deduplicator.TryAccept(1));
        Assert.False(deduplicator.TryAccept(1));
    }
}
