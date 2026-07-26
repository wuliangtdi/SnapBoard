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

    private static ClipboardChangedEvent Change(ulong sequence) =>
        new(sequence, DateTimeOffset.UnixEpoch);
}
