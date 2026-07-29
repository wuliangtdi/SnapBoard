using System.Text.Json;
using System.Text.Json.Serialization;
using SnapBoard.Application.Clipboard;

namespace SnapBoard.Application.Updates;

public static class ApplicationUpdateSettingKeys
{
    public const string Preferences = "application.update.preferences";
}

public enum ApplicationUpdateChannel
{
    Stable = 0,
    Beta = 1,
}

public enum ApplicationUpdateSource
{
    Automatic = 0,
    Official = 1,
    GitHub = 2,
}

public sealed record ApplicationUpdateSettings(
    bool AutomaticChecks,
    ApplicationUpdateChannel Channel,
    ApplicationUpdateSource Source)
{
    public static ApplicationUpdateSettings Default { get; } = new(
        AutomaticChecks: true,
        ApplicationUpdateChannel.Stable,
        ApplicationUpdateSource.Automatic);

    public void Validate()
    {
        if (!Enum.IsDefined(Channel))
        {
            throw new ArgumentOutOfRangeException(nameof(Channel));
        }

        if (!Enum.IsDefined(Source))
        {
            throw new ArgumentOutOfRangeException(nameof(Source));
        }
    }
}

public enum ApplicationUpdateState
{
    Unavailable = 0,
    Idle = 1,
    Checking = 2,
    UpToDate = 3,
    UpdateAvailable = 4,
    Downloading = 5,
    ReadyToInstall = 6,
    Installing = 7,
    Failed = 8,
}

public enum ApplicationUpdateFailure
{
    None = 0,
    NotInstalled = 1,
    UnsupportedPlatform = 2,
    OfficialSourceUnavailable = 3,
    Network = 4,
    InvalidSignature = 5,
    SourceConflict = 6,
    InvalidPackage = 7,
    Busy = 8,
    Unknown = 9,
}

public sealed record ApplicationUpdateStatus(
    ApplicationUpdateState State,
    string CurrentVersion,
    string? AvailableVersion = null,
    string? ReleaseNotes = null,
    int DownloadProgress = 0,
    ApplicationUpdateFailure Failure = ApplicationUpdateFailure.None,
    string? ActiveSource = null)
{
    public static ApplicationUpdateStatus Idle(string currentVersion) => new(
        ApplicationUpdateState.Idle,
        currentVersion);
}

public interface IApplicationUpdateSettingsService
{
    event EventHandler<ApplicationUpdateSettings>? Changed;

    ApplicationUpdateSettings Current { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        ApplicationUpdateSettings settings,
        CancellationToken cancellationToken);
}

public interface IApplicationUpdateService : IDisposable
{
    event EventHandler<ApplicationUpdateStatus>? StatusChanged;

    ApplicationUpdateSettings Settings { get; }

    ApplicationUpdateStatus Status { get; }

    bool IsOfficialSourceConfigured { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken);

    void Start();

    ValueTask UpdateSettingsAsync(
        ApplicationUpdateSettings settings,
        CancellationToken cancellationToken);

    ValueTask CheckForUpdatesAsync(CancellationToken cancellationToken);

    ValueTask DownloadUpdateAsync(CancellationToken cancellationToken);

    void ScheduleInstallAndRestart();
}

public sealed class ApplicationUpdateSettingsService(
    IClipboardHistoryService historyService) : IApplicationUpdateSettingsService, IDisposable
{
    private readonly IClipboardHistoryService _historyService =
        historyService ?? throw new ArgumentNullException(nameof(historyService));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ApplicationUpdateSettings _current = ApplicationUpdateSettings.Default;
    private int _disposed;
    private int _initialized;

    public event EventHandler<ApplicationUpdateSettings>? Changed;

    public ApplicationUpdateSettings Current => Volatile.Read(ref _current);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _initialized) != 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized) != 0)
            {
                return;
            }

            string? json = await _historyService.GetSettingAsync(
                    ApplicationUpdateSettingKeys.Preferences,
                    cancellationToken)
                .ConfigureAwait(false);
            ApplicationUpdateSettings settings = TryDeserialize(json) ??
                ApplicationUpdateSettings.Default;
            Volatile.Write(ref _current, settings);
            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask UpdateAsync(
        ApplicationUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (settings == Current)
            {
                return;
            }

            string json = JsonSerializer.Serialize(
                settings,
                ApplicationUpdateJsonContext.Default.ApplicationUpdateSettings);
            await _historyService.SetSettingAsync(
                    ApplicationUpdateSettingKeys.Preferences,
                    json,
                    cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _current, settings);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, settings);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ApplicationUpdateSettings? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096)
        {
            return null;
        }

        try
        {
            ApplicationUpdateSettings? settings = JsonSerializer.Deserialize(
                json,
                ApplicationUpdateJsonContext.Default.ApplicationUpdateSettings);
            settings?.Validate();
            return settings;
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(ApplicationUpdateSettings))]
internal sealed partial class ApplicationUpdateJsonContext : JsonSerializerContext;
