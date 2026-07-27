using SnapBoard.Application.Clipboard;
using SnapBoard.Desktop.ViewModels;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class RuntimeMainViewModelTests
{
    [Fact]
    public async Task RuntimeViewModelLoadsHistoryIncrementally()
    {
        ClipboardHistoryItemSummary[] summaries = Enumerable.Range(0, 75)
            .Select(index => CreateSummary($"item-{index:D2}", DateTimeOffset.UtcNow.AddSeconds(-index)))
            .ToArray();
        FakeHistoryService service = new()
        {
            SearchHandler = (query, _) => ValueTask.FromResult(query.Cursor is null
                ? new ClipboardHistoryPage(
                    summaries[..50],
                    new ClipboardHistoryCursor(
                        false,
                        summaries[49].CapturedAt.ToUnixTimeMilliseconds(),
                        summaries[49].Id),
                    summaries.Length)
                : new ClipboardHistoryPage(summaries[50..], null, summaries.Length)),
        };
        using MainViewModel viewModel = new(service);

        viewModel.Start();
        await viewModel.WaitForIdleAsync();

        Assert.Equal(50, viewModel.VisibleItems.Count);
        Assert.True(viewModel.CanLoadMore);
        Assert.Equal("共 75 条记录", viewModel.RecordCountText);

        await viewModel.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(75, viewModel.VisibleItems.Count);
        Assert.False(viewModel.CanLoadMore);
        Assert.Equal(2, service.SearchCount);
    }

    [Fact]
    public async Task CancelledOlderSearchCannotOverwriteNewerResults()
    {
        TaskCompletionSource oldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ClipboardHistoryPage> oldResult = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHistoryService service = new()
        {
            SearchHandler = (query, _) => query.SearchText switch
            {
                "old" => WaitForOldAsync(oldStarted, oldResult),
                "new" => ValueTask.FromResult(CreatePage("new-result")),
                _ => ValueTask.FromResult(CreatePage("initial")),
            },
        };
        using MainViewModel viewModel = new(service);
        viewModel.Start();
        await viewModel.WaitForIdleAsync();

        viewModel.SearchText = "old";
        await oldStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
        viewModel.SearchText = "new";
        await viewModel.WaitForIdleAsync();
        oldResult.SetResult(CreatePage("old-result"));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        ClipboardHistoryItemViewModel item = Assert.Single(viewModel.VisibleItems);
        Assert.Equal("new-result", item.Title);
    }

    [Fact]
    public async Task SelectedWriteRequestRestoresStoredFormatsOnDemand()
    {
        ClipboardHistoryItemSummary summary = CreateSummary("multi-format", DateTimeOffset.UtcNow);
        FakeHistoryService service = new()
        {
            SearchHandler = (_, _) => ValueTask.FromResult(new ClipboardHistoryPage(
                [summary],
                null,
                1)),
            Content = new ClipboardHistoryContent(
                summary.Id,
                "plain",
                new byte[] { 1, 2 },
                new byte[] { 3, 4 },
                new ClipboardHistoryBitmap(
                    ClipboardStoredBitmapEncoding.PortableNetworkGraphics,
                    new byte[] { 5, 6 },
                    2,
                    3,
                    32),
                ["C:\\Temp\\one.txt"]),
        };
        using MainViewModel viewModel = new(service);
        viewModel.Start();
        await viewModel.WaitForIdleAsync();

        ClipboardSelectedWriteRequest? rich = await viewModel.CreateSelectedWriteRequestAsync(
            plainText: false,
            TestContext.Current.CancellationToken);
        ClipboardSelectedWriteRequest? plain = await viewModel.CreateSelectedWriteRequestAsync(
            plainText: true,
            TestContext.Current.CancellationToken);
        Assert.NotNull(rich);
        Assert.NotNull(plain);

        Assert.Equal("plain", rich.Request.Text);
        Assert.Equal([1, 2], rich.Request.Html.ToArray());
        Assert.Equal([3, 4], rich.Request.RichText.ToArray());
        Assert.Equal([5, 6], rich.Request.Bitmap?.Data.ToArray());
        Assert.Equal(["C:\\Temp\\one.txt"], rich.Request.FilePaths);
        Assert.Equal("plain", plain.Request.Text);
        Assert.True(plain.Request.Html.IsEmpty);
        Assert.Null(plain.Request.Bitmap);
        Assert.Equal(2, service.ContentReadCount);
    }

    [Fact]
    public async Task RuntimeViewModelResolvesSourceApplicationMetadataOnce()
    {
        const string executablePath = @"C:\Program Files\Tencent\Weixin\Weixin.exe";
        const string applicationUserModelId = "Tencent.Weixin_test!App";
        ClipboardHistoryItemSummary summary = CreateSummary(
            "source metadata",
            DateTimeOffset.UtcNow,
            executablePath,
            applicationUserModelId);
        FakeHistoryService service = new()
        {
            SearchHandler = (_, _) => ValueTask.FromResult(new ClipboardHistoryPage(
                [summary],
                null,
                1)),
        };
        FakeSourceApplicationMetadataResolver resolver = new();
        using MainViewModel viewModel = new(service, resolver);
        viewModel.Start();
        await viewModel.WaitForIdleAsync();
        ClipboardHistoryItemViewModel item = Assert.Single(viewModel.VisibleItems);

        await viewModel.LoadSourceApplicationMetadataAsync(item);
        await viewModel.LoadSourceApplicationMetadataAsync(item);

        Assert.Equal("微信", item.SourceApplication);
        Assert.Equal(executablePath, item.SourceExecutablePath);
        Assert.True(item.HasSourceIconFallback);
        Assert.Equal(1, resolver.ResolveCount);
        Assert.Equal("test-app", resolver.ProcessName);
        Assert.Equal(executablePath, resolver.ExecutablePath);
        Assert.Equal(applicationUserModelId, resolver.ApplicationUserModelId);
    }

    [Fact]
    public async Task HistoryChangeBurstIsCoalescedIntoOneReload()
    {
        FakeHistoryService service = new()
        {
            SearchHandler = (_, _) => ValueTask.FromResult(CreatePage("coalesced")),
        };
        using MainViewModel viewModel = new(service);
        viewModel.Start();
        await viewModel.WaitForIdleAsync();

        for (int index = 0; index < 10_000; index++)
        {
            service.RaiseChanged(new ClipboardHistoryChangedEvent(
                ClipboardHistoryChangeKind.Added,
                ClipboardItemId.New()));
        }

        await Task.Delay(
            TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);
        await viewModel.WaitForIdleAsync();

        Assert.Equal(2, service.SearchCount);
        Assert.Equal("coalesced", Assert.Single(viewModel.VisibleItems).Title);
    }

    private static async ValueTask<ClipboardHistoryPage> WaitForOldAsync(
        TaskCompletionSource started,
        TaskCompletionSource<ClipboardHistoryPage> result)
    {
        started.TrySetResult();
        return await result.Task;
    }

    private static ClipboardHistoryPage CreatePage(string preview)
    {
        ClipboardHistoryItemSummary item = CreateSummary(preview, DateTimeOffset.UtcNow);
        return new ClipboardHistoryPage([item], null, 1);
    }

    private static ClipboardHistoryItemSummary CreateSummary(
        string preview,
        DateTimeOffset capturedAt,
        string? sourceExecutablePath = null,
        string? sourceApplicationUserModelId = null) => new(
        ClipboardItemId.New(),
        ClipboardContentKind.Text,
        ClipboardHistoryDisplayCategory.Text,
        capturedAt,
        "test-app",
        preview,
        false,
        Array.Empty<string>(),
        0,
        null,
        preview.Length,
        false,
        sourceExecutablePath,
        sourceApplicationUserModelId);

    private sealed class FakeSourceApplicationMetadataResolver :
        IClipboardSourceApplicationMetadataResolver
    {
        public int ResolveCount { get; private set; }

        public string? ProcessName { get; private set; }

        public string? ExecutablePath { get; private set; }

        public string? ApplicationUserModelId { get; private set; }

        public ValueTask<ClipboardSourceApplicationMetadata> ResolveAsync(
            ClipboardSourceApplicationIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCount++;
            ProcessName = identity.ProcessName;
            ExecutablePath = identity.ExecutablePath;
            ApplicationUserModelId = identity.ApplicationUserModelId;
            return ValueTask.FromResult(new ClipboardSourceApplicationMetadata("微信"));
        }
    }

    private sealed class FakeHistoryService : IClipboardHistoryService
    {
        public event EventHandler<ClipboardHistoryChangedEvent>? HistoryChanged;

        public required Func<
            ClipboardHistoryQuery,
            CancellationToken,
            ValueTask<ClipboardHistoryPage>> SearchHandler
        { get; init; }

        public ClipboardHistoryContent? Content { get; init; }

        public int SearchCount { get; private set; }

        public int ContentReadCount { get; private set; }

        public ValueTask<ClipboardHistoryInitializationResult> InitializeAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new ClipboardHistoryInitializationResult(false));

        public ValueTask<ClipboardHistoryPage> SearchAsync(
            ClipboardHistoryQuery query,
            CancellationToken cancellationToken)
        {
            SearchCount++;
            return SearchHandler(query, cancellationToken);
        }

        public ValueTask<ClipboardHistoryContent?> GetContentAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken)
        {
            ContentReadCount++;
            return ValueTask.FromResult(Content);
        }

        public ValueTask<ReadOnlyMemory<byte>> GetThumbnailAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
                ReadOnlyMemory<byte>.Empty);

        public ValueTask<bool> SetPinnedAsync(
            ClipboardItemId itemId,
            bool isPinned,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<bool> SetTagsAsync(
            ClipboardItemId itemId,
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<bool> DeleteAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<int> ClearAsync(
            bool includePinned,
            CancellationToken cancellationToken) => ValueTask.FromResult(0);

        public ValueTask<bool> RecordUseAsync(
            ClipboardItemId itemId,
            DateTimeOffset usedAt,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<string?> GetSettingAsync(
            string key,
            CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

        public ValueTask SetSettingAsync(
            string key,
            string value,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void RaiseChanged(ClipboardHistoryChangedEvent change) =>
            HistoryChanged?.Invoke(this, change);
    }
}
