using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Updates;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Application.Tests;

public sealed class ApplicationUpdateSettingsServiceTests
{
    [Fact]
    public async Task InitializeUsesSafeDefaultsWhenNoSettingExists()
    {
        FakeHistoryService history = new();
        using ApplicationUpdateSettingsService service = new(history);

        await service.InitializeAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateSettings.Default, service.Current);
    }

    [Fact]
    public async Task UpdatePersistsAndRaisesChangedAfterWrite()
    {
        FakeHistoryService history = new();
        using ApplicationUpdateSettingsService service = new(history);
        ApplicationUpdateSettings expected = new(
            AutomaticChecks: false,
            ApplicationUpdateChannel.Beta,
            ApplicationUpdateSource.GitHub);
        ApplicationUpdateSettings? changed = null;
        service.Changed += (_, settings) => changed = settings;

        await service.UpdateAsync(expected, CancellationToken.None);

        Assert.Equal(expected, service.Current);
        Assert.Equal(expected, changed);
        Assert.Contains("\"channel\":1", history.Settings[ApplicationUpdateSettingKeys.Preferences]);

        using ApplicationUpdateSettingsService reloaded = new(history);
        await reloaded.InitializeAsync(CancellationToken.None);
        Assert.Equal(expected, reloaded.Current);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"automaticChecks\":true,\"channel\":99,\"source\":0}")]
    [InlineData("{\"automaticChecks\":true,\"channel\":0,\"source\":0,\"unknown\":1}")]
    public async Task InitializeRejectsMalformedOrUnsupportedSettings(string stored)
    {
        FakeHistoryService history = new();
        history.Settings[ApplicationUpdateSettingKeys.Preferences] = stored;
        using ApplicationUpdateSettingsService service = new(history);

        await service.InitializeAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateSettings.Default, service.Current);
    }

    private sealed class FakeHistoryService : IClipboardHistoryService
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public event EventHandler<ClipboardHistoryChangedEvent>? HistoryChanged
        {
            add { }
            remove { }
        }

        public ValueTask<string?> GetSettingAsync(
            string key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Settings.GetValueOrDefault(key));
        }

        public ValueTask SetSettingAsync(
            string key,
            string value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Settings[key] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ClipboardHistoryInitializationResult> InitializeAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ClipboardHistoryPage> SearchAsync(
            ClipboardHistoryQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ClipboardHistoryContent?> GetContentAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReadOnlyMemory<byte>> GetThumbnailAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> SetPinnedAsync(
            ClipboardItemId itemId,
            bool isPinned,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> SetTagsAsync(
            ClipboardItemId itemId,
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(
            ClipboardItemId itemId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<int> ClearAsync(
            bool includePinned,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> RecordUseAsync(
            ClipboardItemId itemId,
            DateTimeOffset usedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
