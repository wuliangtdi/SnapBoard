using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Application.Clipboard;

public static class HistorySettingKeys
{
    public const string Capture = "history.capture";
    public const string Retention = "history.retention";

    public static bool IsSynchronized(string key) =>
        string.Equals(key, Capture, StringComparison.Ordinal) ||
        string.Equals(key, Retention, StringComparison.Ordinal);
}

public sealed record HistoryCaptureSettings(
    bool Text = true,
    bool RichText = true,
    bool Images = true,
    bool Files = true)
{
    public static HistoryCaptureSettings Default { get; } = new();

    internal IReadOnlySet<ClipboardContentKind> ToEnabledContentKinds()
    {
        List<ClipboardContentKind> kinds = [];
        if (Text)
        {
            kinds.Add(ClipboardContentKind.Text);
        }

        if (RichText)
        {
            kinds.Add(ClipboardContentKind.Html);
            kinds.Add(ClipboardContentKind.RichText);
        }

        if (Images)
        {
            kinds.Add(ClipboardContentKind.Image);
        }

        if (Files)
        {
            kinds.Add(ClipboardContentKind.FileReference);
        }

        return kinds.ToFrozenSet();
    }
}

public sealed record HistoryRetentionSettings(
    bool Enabled = false,
    int RetentionDays = 30)
{
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 3650;

    public static HistoryRetentionSettings Default { get; } = new();

    internal ClipboardRetentionPolicy? ToPolicy()
    {
        Validate();
        return Enabled
            ? new ClipboardRetentionPolicy(
                int.MaxValue,
                TimeSpan.FromDays(RetentionDays),
                long.MaxValue)
            : null;
    }

    internal void Validate()
    {
        if (RetentionDays is < MinimumRetentionDays or > MaximumRetentionDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RetentionDays),
                $"Retention days must be between {MinimumRetentionDays} and {MaximumRetentionDays}.");
        }
    }
}

public sealed record HistorySettingsSnapshot(
    HistoryCaptureSettings Capture,
    HistoryRetentionSettings Retention)
{
    public static HistorySettingsSnapshot Default { get; } = new(
        HistoryCaptureSettings.Default,
        HistoryRetentionSettings.Default);
}

public sealed record HistorySettingsChangedEvent(
    HistorySettingsSnapshot Settings,
    string ChangedKey);

public interface IHistorySettingsService
{
    event EventHandler<HistorySettingsChangedEvent>? Changed;

    HistorySettingsSnapshot Current { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        HistoryCaptureSettings capture,
        HistoryRetentionSettings retention,
        CancellationToken cancellationToken);

    ValueTask ApplyRemoteSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken);

    ValueTask PublishCurrentSettingsAsync(CancellationToken cancellationToken);

    ValueTask<int> ApplyRetentionNowAsync(CancellationToken cancellationToken);
}

public sealed class HistorySettingsService : IHistorySettingsService, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan RetentionSweepInterval = TimeSpan.FromHours(24);
    private readonly ClipboardCaptureOptions _captureOptions;
    private readonly IClipboardHistoryService _historyService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly ClipboardHistoryChangeNotifier _notifier;
    private readonly IClipboardHistoryStore _store;
    private HistorySettingsSnapshot _current = HistorySettingsSnapshot.Default;
    private Task? _retentionTask;
    private int _disposed;
    private int _initialized;

    public HistorySettingsService(
        IClipboardHistoryService historyService,
        IClipboardHistoryStore store,
        ClipboardCaptureOptions captureOptions,
        ClipboardHistoryChangeNotifier notifier)
    {
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _captureOptions = captureOptions ?? throw new ArgumentNullException(nameof(captureOptions));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    public event EventHandler<HistorySettingsChangedEvent>? Changed;

    public HistorySettingsSnapshot Current => Volatile.Read(ref _current);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) != 0)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized) != 0)
            {
                return;
            }

            string? captureJson = await _historyService
                .GetSettingAsync(HistorySettingKeys.Capture, cancellationToken)
                .ConfigureAwait(false);
            string? retentionJson = await _historyService
                .GetSettingAsync(HistorySettingKeys.Retention, cancellationToken)
                .ConfigureAwait(false);
            HistoryCaptureSettings capture = TryDeserializeCapture(captureJson) ??
                HistoryCaptureSettings.Default;
            HistoryRetentionSettings retention = TryDeserializeRetention(retentionJson) ??
                HistoryRetentionSettings.Default;
            ApplySnapshot(new HistorySettingsSnapshot(capture, retention));
            Volatile.Write(ref _initialized, 1);
            _retentionTask = Task.Run(
                () => RetentionLoopAsync(_lifetime.Token),
                CancellationToken.None);
        }
        finally
        {
            _initializationGate.Release();
        }

        await TryApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpdateAsync(
        HistoryCaptureSettings capture,
        HistoryRetentionSettings retention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(retention);
        retention.Validate();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (capture != Current.Capture)
            {
                await _historyService.SetSettingAsync(
                        HistorySettingKeys.Capture,
                        SerializeCapture(capture),
                        cancellationToken)
                    .ConfigureAwait(false);
                ApplySnapshot(new HistorySettingsSnapshot(capture, Current.Retention));
                Changed?.Invoke(
                    this,
                    new HistorySettingsChangedEvent(Current, HistorySettingKeys.Capture));
            }

            if (retention != Current.Retention)
            {
                await _historyService.SetSettingAsync(
                        HistorySettingKeys.Retention,
                        SerializeRetention(retention),
                        cancellationToken)
                    .ConfigureAwait(false);
                ApplySnapshot(new HistorySettingsSnapshot(Current.Capture, retention));
                Changed?.Invoke(
                    this,
                    new HistorySettingsChangedEvent(Current, HistorySettingKeys.Retention));
                await TryApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async ValueTask ApplyRemoteSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(key, HistorySettingKeys.Capture, StringComparison.Ordinal))
            {
                HistoryCaptureSettings capture = DeserializeCapture(value);
                ApplySnapshot(new HistorySettingsSnapshot(capture, Current.Retention));
            }
            else if (string.Equals(key, HistorySettingKeys.Retention, StringComparison.Ordinal))
            {
                HistoryRetentionSettings retention = DeserializeRetention(value);
                ApplySnapshot(new HistorySettingsSnapshot(Current.Capture, retention));
                await TryApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentException("The setting is not synchronized.", nameof(key));
            }

            Changed?.Invoke(this, new HistorySettingsChangedEvent(Current, key));
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async ValueTask PublishCurrentSettingsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HistorySettingsSnapshot current = Current;
            await _historyService.SetSettingAsync(
                    HistorySettingKeys.Capture,
                    SerializeCapture(current.Capture),
                    cancellationToken)
                .ConfigureAwait(false);
            await _historyService.SetSettingAsync(
                    HistorySettingKeys.Retention,
                    SerializeRetention(current.Retention),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async ValueTask<int> ApplyRetentionNowAsync(CancellationToken cancellationToken)
    {
        ClipboardRetentionPolicy? policy = Current.Retention.ToPolicy();
        if (policy is null)
        {
            return 0;
        }

        int deleted = await _store.ApplyRetentionAsync(
                policy,
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        if (deleted > 0)
        {
            _notifier.Publish(new ClipboardHistoryChangedEvent(ClipboardHistoryChangeKind.Cleared));
        }

        return deleted;
    }

    public static bool IsValidSynchronizedValue(string? key, string? value)
    {
        if (key is null || value is null || value.Length > 4096)
        {
            return false;
        }

        try
        {
            if (string.Equals(key, HistorySettingKeys.Capture, StringComparison.Ordinal))
            {
                _ = DeserializeCapture(value);
                return true;
            }

            if (string.Equals(key, HistorySettingKeys.Retention, StringComparison.Ordinal))
            {
                _ = DeserializeRetention(value);
                return true;
            }
        }
        catch (JsonException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_retentionTask is not null)
        {
            try
            {
                await _retentionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        _initializationGate.Dispose();
        _updateGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string SerializeCapture(HistoryCaptureSettings settings) =>
        JsonSerializer.Serialize(settings, HistorySettingsJsonContext.Default.HistoryCaptureSettings);

    private static string SerializeRetention(HistoryRetentionSettings settings) =>
        JsonSerializer.Serialize(settings, HistorySettingsJsonContext.Default.HistoryRetentionSettings);

    private static HistoryCaptureSettings DeserializeCapture(string value) =>
        JsonSerializer.Deserialize(
            value,
            HistorySettingsJsonContext.Default.HistoryCaptureSettings) ??
        throw new JsonException("Capture settings are empty.");

    private static HistoryRetentionSettings DeserializeRetention(string value)
    {
        HistoryRetentionSettings settings = JsonSerializer.Deserialize(
                value,
                HistorySettingsJsonContext.Default.HistoryRetentionSettings) ??
            throw new JsonException("Retention settings are empty.");
        settings.Validate();
        return settings;
    }

    private static HistoryCaptureSettings? TryDeserializeCapture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return DeserializeCapture(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HistoryRetentionSettings? TryDeserializeRetention(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return DeserializeRetention(value);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void ApplySnapshot(HistorySettingsSnapshot snapshot)
    {
        _captureOptions.UpdateEnabledContentKinds(snapshot.Capture.ToEnabledContentKinds());
        Volatile.Write(ref _current, snapshot);
    }

    private async ValueTask TryApplyRetentionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ApplyRetentionNowAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 设置已经持久化；清理失败留给手动操作或下一次周期维护重试。
        }
    }

    private async Task RetentionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(RetentionSweepInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ApplyRetentionNowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 周期维护失败留待下次重试；异常不得暴露本地路径或正文。
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(HistoryCaptureSettings))]
[JsonSerializable(typeof(HistoryRetentionSettings))]
internal sealed partial class HistorySettingsJsonContext : JsonSerializerContext;
