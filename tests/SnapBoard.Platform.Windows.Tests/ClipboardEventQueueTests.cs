using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.Windows.Clipboard;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class ClipboardEventQueueTests
{
    [Fact]
    public async Task OverflowDropsOldestEventAndKeepsLatestState()
    {
        ClipboardEventQueue queue = new(2);

        Assert.True(queue.TryWrite(Change(1)));
        Assert.True(queue.TryWrite(Change(2)));
        Assert.True(queue.TryWrite(Change(3)));
        queue.Complete();

        List<ulong> sequences = [];
        await foreach (ClipboardChangedEvent change in queue.ReadAllAsync(CancellationToken.None))
        {
            sequences.Add(change.SequenceNumber);
        }

        Assert.Equal([2UL, 3UL], sequences);
        Assert.Equal(1, queue.DroppedEventCount);
    }

    private static ClipboardChangedEvent Change(ulong sequence) =>
        new(sequence, DateTimeOffset.UnixEpoch);
}
