using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class WindowsClipboardAdapterLifecycleTests
{
    [Fact]
    public async Task WatchStartsHostAndDisposalStopsIt()
    {
        FakeClipboardMessageHost host = new();
        await using WindowsClipboardAdapter adapter = CreateAdapter(host);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await host.Started.Task.WaitAsync(cancellation.Token);
        host.RaiseClipboardUpdated(17);

        Assert.True(await moveNext);
        Assert.Equal(17UL, enumerator.Current.SequenceNumber);

        await enumerator.DisposeAsync();

        Assert.Equal(1, host.StartCount);
        Assert.Equal(1, host.StopCount);
    }

    [Fact]
    public async Task DuplicateSequenceIsNotPublishedTwice()
    {
        FakeClipboardMessageHost host = new();
        await using WindowsClipboardAdapter adapter = CreateAdapter(host);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> firstMove = enumerator.MoveNextAsync().AsTask();
        await host.Started.Task.WaitAsync(cancellation.Token);
        host.RaiseClipboardUpdated(9);
        Assert.True(await firstMove);

        Task<bool> secondMove = enumerator.MoveNextAsync().AsTask();
        host.RaiseClipboardUpdated(9);
        host.RaiseClipboardUpdated(10);

        Assert.True(await secondMove);
        Assert.Equal(10UL, enumerator.Current.SequenceNumber);
    }

    [Fact]
    public async Task CancellationDuringStartupStopsMessageHost()
    {
        CancelableStartupClipboardMessageHost host = new();
        await using WindowsClipboardAdapter adapter = new(
            new WindowsClipboardOptions().ToSettings(),
            host,
            new FakeWindowsPasteNative());
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await host.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await moveNext);
        Assert.Equal(1, host.StopCount);
    }

    [Fact]
    public async Task MessageLoopFailureIsPropagatedToWatcher()
    {
        FakeClipboardMessageHost host = new();
        await using WindowsClipboardAdapter adapter = CreateAdapter(host);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await host.Started.Task.WaitAsync(cancellation.Token);
        host.FailMessageLoop(new InvalidOperationException("message loop failed"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await moveNext);
        Assert.Equal("message loop failed", error.Message);
        Assert.Equal(1, host.StopCount);
    }

    private static WindowsClipboardAdapter CreateAdapter(FakeClipboardMessageHost host) =>
        new(
            new WindowsClipboardOptions { EventQueueCapacity = 4 }.ToSettings(),
            host,
            new FakeWindowsPasteNative());
}
