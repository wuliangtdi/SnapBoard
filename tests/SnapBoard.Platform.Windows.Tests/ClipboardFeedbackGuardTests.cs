using SnapBoard.Platform.Windows.Clipboard;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class ClipboardFeedbackGuardTests
{
    [Fact]
    public void SelfWrittenSequenceIsConsumedOnlyOnce()
    {
        ClipboardFeedbackGuard guard = new();

        guard.Remember(42);

        Assert.True(guard.TryConsume(42));
        Assert.False(guard.TryConsume(42));
        Assert.False(guard.TryConsume(43));
    }

    [Fact]
    public void CapacityEvictsOldestSequence()
    {
        ClipboardFeedbackGuard guard = new(2);

        guard.Remember(1);
        guard.Remember(2);
        guard.Remember(3);

        Assert.False(guard.TryConsume(1));
        Assert.True(guard.TryConsume(2));
        Assert.True(guard.TryConsume(3));
    }
}
