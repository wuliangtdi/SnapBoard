using System.Text.Json;
using System.Text.Json.Serialization;
using SnapBoard.Application.Clipboard;

namespace SnapBoard.Application.Sync;

public static class SyncSettingKeys
{
    public const string PollInterval = "sync.pollInterval";
}

public sealed record SyncPollingSettings(int PollIntervalSeconds = 5 * 60)
{
    public const int DefaultPollIntervalSeconds = 5 * 60;
    public const int MinimumPollIntervalSeconds = 10;
    public const int MaximumPollIntervalSeconds = 60 * 60;

    public static SyncPollingSettings Default { get; } = new();

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

    internal static SyncPollingSettings FromTimeSpan(TimeSpan interval)
    {
        if (interval < TimeSpan.FromSeconds(MinimumPollIntervalSeconds) ||
            interval > TimeSpan.FromSeconds(MaximumPollIntervalSeconds) ||
            interval.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                $"The sync polling interval must use whole seconds between {MinimumPollIntervalSeconds} and {MaximumPollIntervalSeconds}.");
        }

        SyncPollingSettings settings = new(checked((int)interval.TotalSeconds));
        settings.Validate();
        return settings;
    }

    internal void Validate()
    {
        if (PollIntervalSeconds is
            < MinimumPollIntervalSeconds or > MaximumPollIntervalSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollIntervalSeconds),
                $"The sync polling interval must be between {MinimumPollIntervalSeconds} and {MaximumPollIntervalSeconds} seconds.");
        }
    }
}

public sealed record SyncPollingSettingsChangedEvent(SyncPollingSettings Settings);

public static class SynchronizedSettingRegistry
{
    public static bool IsSynchronized(string? key) =>
        key is not null &&
        (HistorySettingKeys.IsSynchronized(key) ||
         string.Equals(key, SyncSettingKeys.PollInterval, StringComparison.Ordinal));

    public static bool IsValidValue(string? key, string? value)
    {
        if (!IsSynchronized(key) || value is null)
        {
            return false;
        }

        return HistorySettingKeys.IsSynchronized(key!)
            ? HistorySettingsService.IsValidSynchronizedValue(key, value)
            : SyncPollingSettingsSerializer.TryDeserialize(value, out _);
    }
}

internal static class SyncPollingSettingsSerializer
{
    private const int MaximumValueCharacters = 4096;

    public static string Serialize(SyncPollingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        return JsonSerializer.Serialize(
            settings,
            SyncPollingSettingsJsonContext.Default.SyncPollingSettings);
    }

    public static SyncPollingSettings Deserialize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumValueCharacters)
        {
            throw new JsonException("Sync polling settings are too large.");
        }

        SyncPollingSettings settings = JsonSerializer.Deserialize(
                value,
                SyncPollingSettingsJsonContext.Default.SyncPollingSettings) ??
            throw new JsonException("Sync polling settings are empty.");
        settings.Validate();
        return settings;
    }

    public static bool TryDeserialize(string? value, out SyncPollingSettings? settings)
    {
        settings = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            settings = Deserialize(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(SyncPollingSettings))]
internal sealed partial class SyncPollingSettingsJsonContext : JsonSerializerContext;
