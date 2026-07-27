using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class ClipboardCaptureCoordinatorTests
{
    [Fact]
    public async Task PausedCaptureKeepsDrainingEventsWithoutReadingPayloads()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new();
        using ClipboardCaptureCoordinator coordinator = new(monitor, reader);
        TaskCompletionSource observedPausedBatch =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += state =>
        {
            if (state.ObservedEventCount >= 100)
            {
                observedPausedBatch.TrySetResult();
            }
        };

        coordinator.SetPaused(paused: true);
        coordinator.Start();
        for (ulong sequence = 1; sequence <= 100; sequence++)
        {
            monitor.Publish(sequence);
        }

        await observedPausedBatch.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, reader.ReadCount);

        coordinator.SetPaused(paused: false);
        monitor.Publish(101);
        await reader.FirstRead.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, reader.ReadCount);
    }

    private sealed class FakeClipboardMonitor : IClipboardMonitor
    {
        private readonly Channel<ClipboardChangedEvent> _events = Channel.CreateUnbounded<ClipboardChangedEvent>();

        public async IAsyncEnumerable<ClipboardChangedEvent> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (ClipboardChangedEvent change in
                _events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Publish(ulong sequenceNumber) => _events.Writer.TryWrite(
            new ClipboardChangedEvent(sequenceNumber, DateTimeOffset.UtcNow));
    }

    private sealed class FakeClipboardReader : IClipboardContentReader
    {
        private int _readCount;

        public TaskCompletionSource FirstRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask<ClipboardReadResult> ReadAsync(
            ClipboardChangedEvent change,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            FirstRead.TrySetResult();
            return ValueTask.FromResult(new ClipboardReadResult(
                ClipboardReadStatus.Failed,
                null,
                ClipboardReadFailureReason.NativeFailure));
        }
    }
}
