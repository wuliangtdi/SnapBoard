using System.Security.Cryptography;
using System.Text;
using SnapBoard.Application.Clipboard;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Infrastructure.Tests;

public sealed class StorageMigrationBarrierTests
{
    [Fact]
    public async Task BarrierCheckpointsDatabaseAndRejectsNewReadsAndWrites()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        ClipboardCapturedItem item = CreateTextItem("before migration");
        ClipboardHistorySaveResult saved = await context.Store.SaveAsync(
            item,
            CancellationToken.None);

        await context.Store.PrepareForMigrationAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.Store.SetPinnedAsync(saved.ItemId, true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await context.Store.SearchAsync(
                new ClipboardHistoryQuery { PageSize = 10 },
                CancellationToken.None));
        string walPath = $"{context.Paths.DatabasePath}-wal";
        Assert.True(!File.Exists(walPath) || new FileInfo(walPath).Length == 0);
    }

    private static ClipboardCapturedItem CreateTextItem(string text)
    {
        ClipboardItemId id = ClipboardItemId.New();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = DateTimeOffset.UtcNow,
            SourceProcessName = "migration-test",
            SourceAccessStatus = 0,
            ContentHash = new ClipboardContentHash(
                Convert.ToHexStringLower(SHA256.HashData(bytes))),
            PrimaryKind = ClipboardContentKind.Text,
            DisplayCategory = ClipboardHistoryDisplayCategory.Text,
            PreviewText = text,
            SearchableText = text,
            Representations =
            [
                new ClipboardCapturedRepresentation(
                    ClipboardContentKind.Text,
                    "text/plain; charset=utf-8",
                    text,
                    default),
            ],
            Formats = [new ClipboardCapturedFormat("text", "Text", true)],
            TotalSizeBytes = bytes.Length,
        };
    }
}
