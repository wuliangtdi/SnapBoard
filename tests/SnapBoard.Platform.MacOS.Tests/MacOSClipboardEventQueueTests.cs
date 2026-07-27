using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Clipboard;

namespace SnapBoard.Platform.MacOS.Tests;

public sealed class MacOSClipboardEventQueueTests
{
    [Fact]
    public async Task FullQueueDropsOldestAndKeepsNewestEvent()
    {
        MacOSClipboardEventQueue queue = new(2);
        queue.TryWrite(Change(1));
        queue.TryWrite(Change(2));
        queue.TryWrite(Change(3));
        queue.Complete();

        List<ulong> sequences = [];
        await foreach (ClipboardChangedEvent change in queue.ReadAllAsync(CancellationToken.None))
        {
            sequences.Add(change.SequenceNumber);
        }

        Assert.Equal([2UL, 3UL], sequences);
        Assert.Equal(1, queue.DroppedEventCount);
    }

    [Fact]
    public async Task TenThousandEventsAreAccountedForWithoutBlockingProducer()
    {
        const int eventCount = 10_000;
        const int capacity = 256;
        MacOSClipboardEventQueue queue = new(capacity);

        for (ulong sequence = 1; sequence <= eventCount; sequence++)
        {
            Assert.True(queue.TryWrite(Change(sequence)));
        }

        queue.Complete();
        List<ulong> retainedSequences = [];
        await foreach (ClipboardChangedEvent change in queue.ReadAllAsync(CancellationToken.None))
        {
            retainedSequences.Add(change.SequenceNumber);
        }

        Assert.Equal(capacity, retainedSequences.Count);
        Assert.Equal((ulong)(eventCount - capacity + 1), retainedSequences[0]);
        Assert.Equal((ulong)eventCount, retainedSequences[^1]);
        Assert.Equal(eventCount, retainedSequences.Count + queue.DroppedEventCount);
    }

    private static ClipboardChangedEvent Change(ulong sequence) =>
        new(sequence, DateTimeOffset.UnixEpoch);
}
