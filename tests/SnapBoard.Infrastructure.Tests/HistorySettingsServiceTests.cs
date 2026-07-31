using System.Security.Cryptography;
using System.Text;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Sync;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Infrastructure.Tests;

public sealed class HistorySettingsServiceTests
{
    [Fact]
    public async Task DefaultsKeepHistoryAndSavedPolicyFiltersCaptureAndEmitsSyncedDeletes()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        ClipboardHistoryChangeNotifier notifier = new();
        ClipboardHistoryService history = new(context.Store, notifier);
        ClipboardCaptureOptions captureOptions = new();
        await using HistorySettingsService settings = new(
            history,
            context.Store,
            captureOptions,
            notifier);
        await settings.InitializeAsync(CancellationToken.None);

        Assert.False(settings.Current.Retention.Enabled);
        Assert.True(settings.Current.Retention.PreserveFavorites);
        Assert.Equal(
            Enum.GetValues<ClipboardContentKind>().Order(),
            captureOptions.EnabledContentKinds.Order());

        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-45);
        ClipboardCapturedItem pinned = CreateTextItem("pinned-old", old);
        ClipboardCapturedItem removable = CreateTextItem("removable-old", old.AddMinutes(1));
        await context.Store.SaveAsync(pinned, CancellationToken.None);
        await context.Store.SaveAsync(removable, CancellationToken.None);
        Assert.True(await context.Store.SetPinnedAsync(
            pinned.Id,
            true,
            CancellationToken.None));

        Assert.Equal(0, await settings.ApplyRetentionNowAsync(CancellationToken.None));
        Assert.Equal(2, (await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None)).Items.Count);

        await context.Store.ConfigureAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            keyVersion: 1,
            enabled: true,
            CancellationToken.None);
        await settings.UpdateAsync(
            new HistoryCaptureSettings(
                Text: true,
                RichText: false,
                Images: false,
                Files: false),
            new HistoryRetentionSettings(Enabled: true, RetentionDays: 30),
            CancellationToken.None);

        Assert.Equal([ClipboardContentKind.Text], captureOptions.EnabledContentKinds);
        ClipboardHistoryItemSummary remaining = Assert.Single((await context.Store.SearchAsync(
            new ClipboardHistoryQuery { PageSize = 10 },
            CancellationToken.None)).Items);
        Assert.Equal(pinned.Id, remaining.Id);
        Assert.True(remaining.IsPinned);
        Assert.NotNull(await context.Store.GetSettingAsync(
            HistorySettingKeys.Capture,
            CancellationToken.None));
        Assert.NotNull(await context.Store.GetSettingAsync(
            HistorySettingKeys.Retention,
            CancellationToken.None));

        IReadOnlyList<SyncOutboxItem> outbox = await context.Store.ReadOutboxBatchAsync(
            10,
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        try
        {
            Assert.Equal(3, outbox.Count);
            Assert.Equal(
                [SyncChangeKind.SetSetting, SyncChangeKind.SetSetting, SyncChangeKind.Delete],
                outbox.Select(item => item.Event.ChangeKind));
            Assert.Equal(removable.Id.Value, outbox[2].Event.ItemId);
        }
        finally
        {
            foreach (SyncOutboxItem item in outbox)
            {
                CryptographicOperations.ZeroMemory(item.SerializedEvent);
            }
        }
    }

    [Fact]
    public async Task RetentionFavoritePreferencePersistsAcrossRestart()
    {
        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync();
        ClipboardHistoryChangeNotifier notifier = new();
        ClipboardHistoryService history = new(context.Store, notifier);
        ClipboardCapturedItem favorite = CreateTextItem(
            "favorite-to-clean",
            DateTimeOffset.UtcNow.AddDays(-90));
        await context.Store.SaveAsync(favorite, CancellationToken.None);
        Assert.True(await context.Store.SetPinnedAsync(
            favorite.Id,
            true,
            CancellationToken.None));

        await using (HistorySettingsService first = new(
            history,
            context.Store,
            new ClipboardCaptureOptions(),
            notifier))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.UpdateAsync(
                HistoryCaptureSettings.Default,
                new HistoryRetentionSettings(
                    Enabled: true,
                    RetentionDays: 60,
                    PreserveFavorites: false),
                CancellationToken.None);
            Assert.Empty((await context.Store.SearchAsync(
                new ClipboardHistoryQuery { PageSize = 10 },
                CancellationToken.None)).Items);
        }

        await using HistorySettingsService restarted = new(
            history,
            context.Store,
            new ClipboardCaptureOptions(),
            notifier);
        await restarted.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            new HistoryRetentionSettings(
                Enabled: true,
                RetentionDays: 60,
                PreserveFavorites: false),
            restarted.Current.Retention);
    }

    private static ClipboardCapturedItem CreateTextItem(
        string text,
        DateTimeOffset capturedAt)
    {
        ClipboardItemId id = ClipboardItemId.New();
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return new ClipboardCapturedItem
        {
            Id = id,
            SequenceNumber = BitConverter.ToUInt64(id.Value.ToByteArray(), 0),
            CapturedAt = capturedAt,
            SourceProcessName = "history-settings-test",
            ContentHash = new ClipboardContentHash(
                Convert.ToHexStringLower(SHA256.HashData(utf8))),
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
            TotalSizeBytes = utf8.LongLength,
        };
    }
}
