using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SnapBoard.Application.Clipboard;
using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class ClipboardCaptureCoordinatorTests
{
    [Fact]
    public async Task ReadResultsArePassedToApplicationCaptureService()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new()
        {
            Result = new ClipboardReadResult(
                ClipboardReadStatus.Success,
                new ClipboardContentSnapshot
                {
                    SequenceNumber = 42,
                    CapturedAt = DateTimeOffset.UtcNow,
                    Source = new ClipboardSourceInfo(
                        1,
                        "test",
                        null,
                        ClipboardSourceAccessStatus.Identified),
                    Text = "captured",
                }),
        };
        RecordingCaptureService captureService = new();
        using ClipboardCaptureCoordinator coordinator = new(monitor, reader, captureService);

        coordinator.Start();
        monitor.Publish(42);
        ClipboardReadResult captured = await captureService.Captured.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(42UL, captured.Snapshot?.SequenceNumber);
        Assert.Equal(1, captureService.ProcessCount);
    }

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

    [Fact]
    public async Task ReaderFailureDoesNotExposeExceptionMessageToUiState()
    {
        FakeClipboardMonitor monitor = new();
        FakeClipboardReader reader = new()
        {
            Exception = new InvalidOperationException("sensitive clipboard content"),
        };
        using ClipboardCaptureCoordinator coordinator = new(monitor, reader);
        TaskCompletionSource<string> failure = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += state =>
        {
            if (state.ErrorMessage is not null)
            {
                failure.TrySetResult(state.ErrorMessage);
            }
        };

        coordinator.Start();
        monitor.Publish(1);
        string error = await failure.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal("clipboard-capture-failed", error);
        Assert.DoesNotContain("sensitive", error, StringComparison.OrdinalIgnoreCase);
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

        public ClipboardReadResult Result { get; init; } = new(
            ClipboardReadStatus.Failed,
            null,
            ClipboardReadFailureReason.NativeFailure);

        public Exception? Exception { get; init; }

        public ValueTask<ClipboardReadResult> ReadAsync(
            ClipboardChangedEvent change,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            FirstRead.TrySetResult();
            if (Exception is not null)
            {
                throw Exception;
            }

            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingCaptureService : IClipboardCaptureService
    {
        private int _processCount;

        public TaskCompletionSource<ClipboardReadResult> Captured { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessCount => Volatile.Read(ref _processCount);

        public ValueTask<ClipboardCaptureResult> ProcessAsync(
            ClipboardReadResult readResult,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _processCount);
            Captured.TrySetResult(readResult);
            return ValueTask.FromResult(new ClipboardCaptureResult(
                ClipboardCaptureStatus.Stored,
                "stored"));
        }
    }
}
