using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Platform.MacOS.Tests;

public sealed class MacOSClipboardAdapterLifecycleTests
{
    [Fact]
    public async Task ChangeCountPublishesOnceAndCancellationStopsPolling()
    {
        FakeMacOSPasteboardNative pasteboard = new() { ChangeCount = 10 };
        ControlledAsyncDelay delay = new();
        await using MacOSClipboardAdapter adapter = CreateAdapter(pasteboard, delay);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await delay.WaitForRequestAsync(cancellation.Token);
        pasteboard.ChangeCount = 11;
        delay.ReleaseNext();

        Assert.True(await moveNext.WaitAsync(cancellation.Token));
        Assert.Equal(11UL, enumerator.Current.SequenceNumber);
        Assert.Equal(101, enumerator.Current.SourceHint.ForegroundProcessId);

        await delay.WaitForRequestAsync(cancellation.Token);
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SameChangeCountIsDeduplicated()
    {
        FakeMacOSPasteboardNative pasteboard = new() { ChangeCount = 5 };
        ControlledAsyncDelay delay = new();
        await using MacOSClipboardAdapter adapter = CreateAdapter(pasteboard, delay);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await delay.WaitForRequestAsync(cancellation.Token);
        delay.ReleaseNext();

        await delay.WaitForRequestAsync(cancellation.Token);
        pasteboard.ChangeCount = 6;
        delay.ReleaseNext();

        Assert.True(await moveNext.WaitAsync(cancellation.Token));
        Assert.Equal(6UL, enumerator.Current.SequenceNumber);
    }

    [Fact]
    public async Task MissingForegroundProcessPublishesEmptySourceHint()
    {
        FakeMacOSPasteboardNative pasteboard = new() { ChangeCount = 20 };
        FakeMacOSPasteNative pasteNative = new() { FrontmostProcessId = 0 };
        ControlledAsyncDelay delay = new();
        await using MacOSClipboardAdapter adapter = new(
            MacOSPollingBackoffTests.CreateSettings(),
            pasteboard,
            pasteNative,
            delay);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await delay.WaitForRequestAsync(cancellation.Token);
        pasteboard.ChangeCount = 21;
        delay.ReleaseNext();

        Assert.True(await moveNext.WaitAsync(cancellation.Token));
        Assert.Null(enumerator.Current.SourceHint.ForegroundProcessId);
    }

    [Fact]
    public async Task SelfWriteSequenceIsSuppressedWithoutReadingPayload()
    {
        FakeMacOSPasteboardNative pasteboard = new();
        ControlledAsyncDelay delay = new();
        await using MacOSClipboardAdapter adapter = CreateAdapter(pasteboard, delay);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ClipboardChangedEvent> enumerator =
            adapter.WatchAsync(cancellation.Token).GetAsyncEnumerator();

        Task<bool> moveNext = enumerator.MoveNextAsync().AsTask();
        await delay.WaitForRequestAsync(cancellation.Token);
        ClipboardWriteResult write = await adapter.WritePlainTextAsync("self", cancellation.Token);
        delay.ReleaseNext();

        await delay.WaitForRequestAsync(cancellation.Token);
        Assert.False(moveNext.IsCompleted);
        Assert.Equal(0, pasteboard.ReadCount);
        Assert.True(write.FeedbackMarkerWritten);

        cancellation.Cancel();
        try
        {
            Assert.False(await moveNext.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        catch (OperationCanceledException)
        {
            // ReadAllAsync 可能先观察到取消，也可能先观察到生产者正常完成；两者都表示及时退出。
        }
    }

    [Fact]
    public void SignedChangeCountOverflowPreservesEveryBit()
    {
        Assert.Equal(
            ulong.MaxValue,
            SnapBoard.Platform.MacOS.Clipboard.MacOSClipboardSequence.ToPublicSequence(-1));
    }

    private static MacOSClipboardAdapter CreateAdapter(
        FakeMacOSPasteboardNative pasteboard,
        ControlledAsyncDelay delay) => new(
            MacOSPollingBackoffTests.CreateSettings(),
            pasteboard,
            new FakeMacOSPasteNative(),
            delay);
}
