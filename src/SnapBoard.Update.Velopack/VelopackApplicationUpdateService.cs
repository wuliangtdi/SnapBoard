using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using SnapBoard.Application.Updates;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace SnapBoard.Update.Velopack;

public sealed class VelopackApplicationUpdateService : IApplicationUpdateService
{
    private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(12);
    private readonly IApplicationUpdateSettingsService _settingsService;
    private readonly UpdateEndpointOptions _endpointOptions;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private ApplicationUpdateStatus _status;
    private Task? _backgroundTask;
    private UpdateManager? _manager;
    private CompositeUpdateSource? _source;
    private UpdateInfo? _updateInfo;
    private int _disposed;
    private int _initialized;
    private int _started;

    public VelopackApplicationUpdateService(
        IApplicationUpdateSettingsService settingsService,
        UpdateEndpointOptions? endpointOptions = null,
        TimeProvider? timeProvider = null)
    {
        _settingsService = settingsService ??
            throw new ArgumentNullException(nameof(settingsService));
        _endpointOptions = endpointOptions ?? UpdateEndpointOptions.CreateDefault();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _status = ApplicationUpdateStatus.Idle(GetEntryAssemblyVersion());
        _settingsService.Changed += OnSettingsChanged;
    }

    public event EventHandler<ApplicationUpdateStatus>? StatusChanged;

    public ApplicationUpdateSettings Settings => _settingsService.Current;

    public ApplicationUpdateStatus Status => Volatile.Read(ref _status);

    public bool IsOfficialSourceConfigured => _endpointOptions.OfficialBaseUri is not null;

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

            await _settingsService.InitializeAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (UpdateManager Manager, CompositeUpdateSource Source) created =
                    CreateManager(Settings);
                lock (_stateGate)
                {
                    _manager = created.Manager;
                    _source = created.Source;
                }

                PublishStatus(created.Manager.IsInstalled
                    ? ApplicationUpdateStatus.Idle(GetCurrentVersion(created.Manager))
                    : new ApplicationUpdateStatus(
                        ApplicationUpdateState.Unavailable,
                        GetEntryAssemblyVersion(),
                        Failure: ApplicationUpdateFailure.NotInstalled));
            }
            catch (Exception exception)
            {
                PublishFailure(exception);
            }

            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _backgroundTask = Task.Run(
            () => RunAutomaticChecksAsync(_lifetime.Token),
            CancellationToken.None);
    }

    public async ValueTask UpdateSettingsAsync(
        ApplicationUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _settingsService.UpdateAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (UpdateManager Manager, CompositeUpdateSource Source) created =
                CreateManager(Settings);
            if (!created.Manager.IsInstalled)
            {
                PublishStatus(new ApplicationUpdateStatus(
                    ApplicationUpdateState.Unavailable,
                    GetEntryAssemblyVersion(),
                    Failure: ApplicationUpdateFailure.NotInstalled));
                return;
            }

            lock (_stateGate)
            {
                _manager = created.Manager;
                _source = created.Source;
                _updateInfo = null;
            }

            PublishStatus(new ApplicationUpdateStatus(
                ApplicationUpdateState.Checking,
                GetCurrentVersion(created.Manager)));
            UpdateInfo? update = await created.Manager.CheckForUpdatesAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (update is null)
            {
                PublishStatus(new ApplicationUpdateStatus(
                    ApplicationUpdateState.UpToDate,
                    GetCurrentVersion(created.Manager)));
                return;
            }

            lock (_stateGate)
            {
                _updateInfo = update;
            }

            PublishStatus(new ApplicationUpdateStatus(
                ApplicationUpdateState.UpdateAvailable,
                GetCurrentVersion(created.Manager),
                update.TargetFullRelease.Version.ToFullString(),
                update.TargetFullRelease.NotesMarkdown,
                ActiveSource: created.Source.GetSourceNames(update.TargetFullRelease)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishStatus(ApplicationUpdateStatus.Idle(GetEntryAssemblyVersion()));
            throw;
        }
        catch (Exception exception)
        {
            PublishFailure(exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpdateManager manager;
            UpdateInfo update;
            CompositeUpdateSource source;
            lock (_stateGate)
            {
                manager = _manager ?? throw new InvalidOperationException(
                    "An update check must complete before downloading.");
                update = _updateInfo ?? throw new InvalidOperationException(
                    "No update is available for download.");
                source = _source ?? throw new InvalidOperationException(
                    "The selected update source is unavailable.");
            }

            string currentVersion = GetCurrentVersion(manager);
            string availableVersion = update.TargetFullRelease.Version.ToFullString();
            PublishStatus(new ApplicationUpdateStatus(
                ApplicationUpdateState.Downloading,
                currentVersion,
                availableVersion,
                update.TargetFullRelease.NotesMarkdown));
            int lastProgress = -1;
            await manager.DownloadUpdatesAsync(
                    update,
                    progress =>
                    {
                        int bounded = Math.Clamp(progress, 0, 100);
                        if (Interlocked.Exchange(ref lastProgress, bounded) == bounded)
                        {
                            return;
                        }

                        PublishStatus(new ApplicationUpdateStatus(
                            ApplicationUpdateState.Downloading,
                            currentVersion,
                            availableVersion,
                            update.TargetFullRelease.NotesMarkdown,
                            bounded,
                            ActiveSource: source.LastDownloadSource));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            PublishStatus(new ApplicationUpdateStatus(
                ApplicationUpdateState.ReadyToInstall,
                currentVersion,
                availableVersion,
                update.TargetFullRelease.NotesMarkdown,
                DownloadProgress: 100,
                ActiveSource: source.LastDownloadSource));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishStatus(ApplicationUpdateStatus.Idle(GetEntryAssemblyVersion()));
            throw;
        }
        catch (Exception exception)
        {
            PublishFailure(exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void ScheduleInstallAndRestart()
    {
        ThrowIfDisposed();
        UpdateManager manager;
        UpdateInfo update;
        lock (_stateGate)
        {
            manager = _manager ?? throw new InvalidOperationException(
                "No update manager is ready.");
            update = _updateInfo ?? throw new InvalidOperationException(
                "No downloaded update is ready.");
        }

        ApplicationUpdateStatus status = Status;
        if (status.State != ApplicationUpdateState.ReadyToInstall)
        {
            throw new InvalidOperationException("The update has not finished downloading.");
        }

        PublishStatus(status with { State = ApplicationUpdateState.Installing });
        try
        {
            manager.WaitExitThenApplyUpdates(
                update.TargetFullRelease,
                silent: false,
                restart: true,
                restartArgs: []);
        }
        catch (Exception exception)
        {
            PublishFailure(exception);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _settingsService.Changed -= OnSettingsChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private (UpdateManager Manager, CompositeUpdateSource Source) CreateManager(
        ApplicationUpdateSettings settings)
    {
        settings.Validate();
        IFileDownloader signedDownloader = new SignedFileDownloader(
            new HttpClientFileDownloader(),
            _endpointOptions.PublicKeySubjectPublicKeyInfoBase64);
        List<UpdateSourceDescriptor> descriptors = [];
        if (settings.Source is ApplicationUpdateSource.Automatic or
            ApplicationUpdateSource.Official)
        {
            if (_endpointOptions.OfficialBaseUri is not null)
            {
                descriptors.Add(new UpdateSourceDescriptor(
                    "官方源",
                    new SimpleWebSource(
                        _endpointOptions.OfficialBaseUri,
                        signedDownloader,
                        timeout: 0.5)));
            }
            else if (settings.Source == ApplicationUpdateSource.Official)
            {
                throw new OfficialUpdateSourceUnavailableException();
            }
        }

        if (settings.Source is ApplicationUpdateSource.Automatic or
            ApplicationUpdateSource.GitHub)
        {
            descriptors.Add(new UpdateSourceDescriptor(
                "GitHub",
                new GithubSource(
                    _endpointOptions.GitHubRepository.AbsoluteUri,
                    null,
                    true,
                    signedDownloader)));
        }

        CompositeUpdateSource source = new(descriptors);
        UpdateManager manager = new(
            source,
            new UpdateOptions
            {
                AllowVersionDowngrade = false,
                ExplicitChannel = GetVelopackChannel(settings.Channel),
                MaximumDeltasBeforeFallback = 10,
            });
        return (manager, source);
    }

    private static string GetVelopackChannel(ApplicationUpdateChannel channel)
    {
        string platform = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException();
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(),
        };
        return channel switch
        {
            ApplicationUpdateChannel.Stable => $"{platform}-{architecture}-stable",
            ApplicationUpdateChannel.Beta => $"{platform}-{architecture}-beta",
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }

    private async Task RunAutomaticChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(InitialCheckDelay, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            while (true)
            {
                if (Settings.AutomaticChecks)
                {
                    await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(AutomaticCheckInterval, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnSettingsChanged(object? sender, ApplicationUpdateSettings settings)
    {
        lock (_stateGate)
        {
            _manager = null;
            _source = null;
            _updateInfo = null;
        }

        PublishStatus(ApplicationUpdateStatus.Idle(GetEntryAssemblyVersion()));
    }

    private void PublishFailure(Exception exception)
    {
        ApplicationUpdateFailure failure = exception switch
        {
            NotInstalledException => ApplicationUpdateFailure.NotInstalled,
            PlatformNotSupportedException => ApplicationUpdateFailure.UnsupportedPlatform,
            OfficialUpdateSourceUnavailableException =>
                ApplicationUpdateFailure.OfficialSourceUnavailable,
            UpdateSignatureException => ApplicationUpdateFailure.InvalidSignature,
            UpdateSourceConflictException => ApplicationUpdateFailure.SourceConflict,
            ChecksumFailedException => ApplicationUpdateFailure.InvalidPackage,
            AcquireLockFailedException => ApplicationUpdateFailure.Busy,
            HttpRequestException or WebException or TimeoutException or
                UpdateSourcesUnavailableException => ApplicationUpdateFailure.Network,
            _ => ApplicationUpdateFailure.Unknown,
        };
        PublishStatus(new ApplicationUpdateStatus(
            failure is ApplicationUpdateFailure.NotInstalled or
                ApplicationUpdateFailure.UnsupportedPlatform
                ? ApplicationUpdateState.Unavailable
                : ApplicationUpdateState.Failed,
            GetEntryAssemblyVersion(),
            Failure: failure));
    }

    private void PublishStatus(ApplicationUpdateStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }

    private static string GetCurrentVersion(UpdateManager manager) =>
        manager.CurrentVersion?.ToFullString() ?? GetEntryAssemblyVersion();

    private static string GetEntryAssemblyVersion()
    {
        Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);
}
