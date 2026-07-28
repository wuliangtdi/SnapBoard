using System.Data.Common;
using System.Security.Cryptography;
using System.Threading.Channels;
using SnapBoard.Application.Clipboard;

namespace SnapBoard.Application.Sync;

public sealed class SyncServiceOptions
{
    public SyncServiceOptions(
        TimeSpan? pollInterval = null,
        int maximumUploadBatch = 50,
        int maximumDownloadBatchPerDevice = 100)
    {
        TimeSpan interval = pollInterval ?? SyncPollingSettings.Default.PollInterval;
        _ = SyncPollingSettings.FromTimeSpan(interval);

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUploadBatch, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumUploadBatch, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDownloadBatchPerDevice, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumDownloadBatchPerDevice, 500);
        PollInterval = interval;
        MaximumUploadBatch = maximumUploadBatch;
        MaximumDownloadBatchPerDevice = maximumDownloadBatchPerDevice;
    }

    public TimeSpan PollInterval { get; }

    public int MaximumUploadBatch { get; }

    public int MaximumDownloadBatchPerDevice { get; }
}

public sealed partial class SyncService :
    ISyncService,
    ISyncProviderMigrationService,
    IDisposable,
    IAsyncDisposable
{
    private readonly ISyncCredentialService _credentialService;
    private readonly ClipboardHistoryChangeNotifier? _historyChangeNotifier;
    private readonly IClipboardHistoryService _historyService;
    private readonly IHistorySettingsService? _historySettingsService;
    private readonly ISyncKeyService _keyService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifecycleGate = new();
    private readonly SyncServiceOptions _options;
    private readonly SemaphoreSlim _pollingSettingsGate = new(1, 1);
    private readonly PeriodicTimer _pollingTimer;
    private readonly ISyncObjectProtector _protector;
    private readonly ISyncRecoveryMaterialStore _recoveryMaterialStore;
    private readonly ISyncRemoteSessionFactory _remoteSessionFactory;
    private readonly ISyncRemoteProviderMigrationSessionFactory?
        _providerMigrationSessionFactory;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly ISyncStore _store;
    private readonly Channel<bool> _triggers = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private CancellationTokenSource? _currentFlightCancellation;
    private Task? _periodicTask;
    private SyncPollingSettings _pollingSettings;
    private SyncProviderMigrationSnapshot _providerMigration = new(
        SyncProviderMigrationState.None);
    private Task? _workerTask;
    private SyncStatusSnapshot _status = new(SyncServiceState.NotConfigured);
    private int _disposed;
    private int _paused;
    private int _pollingSettingsInitialized;
    private int _started;

    public SyncService(
        ISyncStore store,
        ISyncKeyService keyService,
        ISyncCredentialService credentialService,
        ISyncRecoveryMaterialStore recoveryMaterialStore,
        ISyncObjectProtector protector,
        ISyncRemoteSessionFactory remoteSessionFactory,
        IClipboardHistoryService historyService,
        SyncServiceOptions? options = null,
        IHistorySettingsService? historySettingsService = null,
        ClipboardHistoryChangeNotifier? historyChangeNotifier = null,
        ISyncRemoteProviderMigrationSessionFactory? providerMigrationSessionFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
        _credentialService = credentialService ??
            throw new ArgumentNullException(nameof(credentialService));
        _recoveryMaterialStore = recoveryMaterialStore ??
            throw new ArgumentNullException(nameof(recoveryMaterialStore));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _remoteSessionFactory = remoteSessionFactory ??
            throw new ArgumentNullException(nameof(remoteSessionFactory));
        _providerMigrationSessionFactory = providerMigrationSessionFactory;
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _options = options ?? new SyncServiceOptions();
        _pollingSettings = SyncPollingSettings.FromTimeSpan(_options.PollInterval);
        _pollingTimer = new PeriodicTimer(_pollingSettings.PollInterval);
        _historySettingsService = historySettingsService;
        _historyChangeNotifier = historyChangeNotifier;
    }

    public event EventHandler<SyncStatusSnapshot>? StatusChanged;

    public event EventHandler<SyncPollingSettingsChangedEvent>? PollingSettingsChanged;

    public SyncStatusSnapshot Status => Volatile.Read(ref _status);

    public SyncPollingSettings PollingSettings => Volatile.Read(ref _pollingSettings);

    public void Start()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _historyService.HistoryChanged += OnHistoryChanged;
        _workerTask = Task.Run(
            () => WorkerLoopAsync(_lifetimeCancellation.Token),
            CancellationToken.None);
        _periodicTask = Task.Run(
            () => PeriodicLoopAsync(_lifetimeCancellation.Token),
            CancellationToken.None);
        RequestSync();
    }

    public bool RequestSync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _paused) != 0)
        {
            return false;
        }

        return _triggers.Writer.TryWrite(true);
    }

    public async ValueTask InitializePollingSettingsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _pollingSettingsInitialized) != 0)
        {
            return;
        }

        await _pollingSettingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _pollingSettingsInitialized) != 0)
            {
                return;
            }

            string? value = await _historyService.GetSettingAsync(
                    SyncSettingKeys.PollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            if (SyncPollingSettingsSerializer.TryDeserialize(value, out var stored))
            {
                ApplyPollingSettings(stored!);
            }

            Volatile.Write(ref _pollingSettingsInitialized, 1);
        }
        finally
        {
            _pollingSettingsGate.Release();
        }
    }

    public async ValueTask UpdatePollingSettingsAsync(
        SyncPollingSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        await InitializePollingSettingsAsync(cancellationToken).ConfigureAwait(false);
        await _pollingSettingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _historyService.SetSettingAsync(
                    SyncSettingKeys.PollInterval,
                    SyncPollingSettingsSerializer.Serialize(settings),
                    cancellationToken)
                .ConfigureAwait(false);
            ApplyPollingSettings(settings);
        }
        finally
        {
            _pollingSettingsGate.Release();
        }
    }

    public ValueTask<SyncStatusSnapshot> SynchronizeNowAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return ExecuteSingleFlightAsync(cancellationToken);
    }

    public ValueTask<SyncSetupResult> CreateSpaceAsync(
        SyncSetupRequest request,
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ConfigureSpaceAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            keyVersion: 1,
            request,
            password,
            recoveryEnvelope: default,
            recoveryCode,
            createNewKey: true,
            cancellationToken);
    }

    public ValueTask<SyncSetupResult> JoinSpaceAsync(
        Guid spaceId,
        int keyVersion,
        SyncSetupRequest request,
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> recoveryEnvelope,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(spaceId, Guid.Empty);
        if (keyVersion is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(keyVersion));
        }

        ArgumentNullException.ThrowIfNull(request);
        if (recoveryEnvelope.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryEnvelope));
        }

        return ConfigureSpaceAsync(
            spaceId,
            Guid.NewGuid(),
            keyVersion,
            request,
            password,
            recoveryEnvelope,
            recoveryCode,
            createNewKey: false,
            cancellationToken);
    }

    public async ValueTask PauseAndDrainAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _paused, 1);
        lock (_lifecycleGate)
        {
            _currentFlightCancellation?.Cancel();
        }

        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        _singleFlight.Release();
        UpdateStatus(Status with
        {
            State = SyncServiceState.Paused,
            DiagnosticCode = null,
        });
    }

    public void ResumeAfterPause()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _paused, 0) == 0)
        {
            return;
        }

        UpdateStatus(Status with
        {
            State = Status.SpaceId is null
                ? SyncServiceState.NotConfigured
                : SyncServiceState.Idle,
            DiagnosticCode = null,
        });
        RequestSync();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _started) != 0)
        {
            _historyService.HistoryChanged -= OnHistoryChanged;
        }

        _lifetimeCancellation.Cancel();
        _pollingTimer.Dispose();
        _triggers.Writer.TryComplete();
        lock (_lifecycleGate)
        {
            _currentFlightCancellation?.Cancel();
        }

        Task[] tasks = [_workerTask ?? Task.CompletedTask, _periodicTask ?? Task.CompletedTask];
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _singleFlight.Dispose();
        _pollingSettingsGate.Dispose();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask<SyncSetupResult> ConfigureSpaceAsync(
        Guid spaceId,
        Guid deviceId,
        int keyVersion,
        SyncSetupRequest request,
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> recoveryEnvelope,
        ReadOnlyMemory<byte> recoveryCode,
        bool createNewKey,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _paused) != 0)
        {
            return new SyncSetupResult(
                SyncSetupStatus.PersistenceFailure,
                DiagnosticCode: "sync-paused");
        }

        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool keyStored = false;
        bool credentialStored = false;
        bool recoveryStored = false;
        bool completed = false;
        byte[]? createdRecoveryEnvelope = null;
        try
        {
            SyncConfigurationSnapshot? existingConfiguration =
                await _store.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (existingConfiguration is not null)
            {
                if (createNewKey ||
                    existingConfiguration.SpaceId != spaceId ||
                    existingConfiguration.KeyVersion != keyVersion)
                {
                    return new SyncSetupResult(
                        SyncSetupStatus.InvalidConfiguration,
                        existingConfiguration.SpaceId,
                        existingConfiguration.DeviceId,
                        DiagnosticCode: "sync-space-already-configured");
                }

                return await ReconfigureExistingSpaceAsync(
                        existingConfiguration,
                        request,
                        password,
                        recoveryEnvelope,
                        recoveryCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            UpdateStatus(new SyncStatusSnapshot(
                SyncServiceState.Synchronizing,
                spaceId,
                DiagnosticCode: "configuring"));
            ReadOnlyMemory<byte> materialToSave;
            if (createNewKey)
            {
                SyncSpaceKeyCreationResult creation = await _keyService.CreateSpaceKeyAsync(
                        spaceId,
                        keyVersion,
                        recoveryCode,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (creation.Status != SyncKeyOperationStatus.Success ||
                    creation.RecoveryEnvelope is null)
                {
                    return SetupFailure(
                        MapKeySetupStatus(creation.Status),
                        spaceId,
                        deviceId,
                        "key-create-failed");
                }

                keyStored = true;
                createdRecoveryEnvelope = creation.RecoveryEnvelope;
                materialToSave = createdRecoveryEnvelope;
            }
            else
            {
                SyncKeyOperationStatus imported = await _keyService.ImportSpaceKeyAsync(
                        spaceId,
                        keyVersion,
                        recoveryEnvelope,
                        recoveryCode,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (imported != SyncKeyOperationStatus.Success)
                {
                    return SetupFailure(
                        MapKeySetupStatus(imported),
                        spaceId,
                        deviceId,
                        "key-import-failed");
                }

                keyStored = true;
                materialToSave = recoveryEnvelope;
            }

            SyncCredentialOperationStatus credentialStatus =
                await _credentialService.StoreAsync(
                        spaceId,
                        request.RemoteConfiguration,
                        password,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (credentialStatus != SyncCredentialOperationStatus.Success)
            {
                return SetupFailure(
                    MapCredentialSetupStatus(credentialStatus),
                    spaceId,
                    deviceId,
                    "credential-store-failed");
            }

            credentialStored = true;
            SyncMasterKeyOpenResult opened = await _keyService.OpenMasterKeyAsync(
                    spaceId,
                    keyVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            if (opened.Status != SyncKeyOperationStatus.Success || opened.Key is null)
            {
                return SetupFailure(
                    MapKeySetupStatus(opened.Status),
                    spaceId,
                    deviceId,
                    "key-open-failed");
            }

            using (opened.Key)
            await using (ISyncRemoteSession session = _remoteSessionFactory.Create(
                request.RemoteConfiguration,
                password))
            {
                await EnsureAndValidateMetadataAsync(
                        session,
                        spaceId,
                        deviceId,
                        keyVersion,
                        opened.Key.Key,
                        createIfMissing: createNewKey,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            string recoveryPath = await _recoveryMaterialStore.SaveAsync(
                    spaceId,
                    keyVersion,
                    materialToSave,
                    cancellationToken)
                .ConfigureAwait(false);
            recoveryStored = true;
            await _store.ConfigureAsync(
                    spaceId,
                    deviceId,
                    keyVersion,
                    enabled: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (createNewKey)
            {
                try
                {
                    if (_historySettingsService is not null)
                    {
                        await _historySettingsService.PublishCurrentSettingsAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await PublishCurrentPollingSettingsAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is
                    InvalidDataException or InvalidOperationException or DbException or
                    IOException or UnauthorizedAccessException)
                {
                    // 空间已经安全创建；设置仍会在用户下次修改时进入同一事件流。
                }
            }

            completed = true;
            SyncStatusSnapshot ready = new(
                SyncServiceState.Idle,
                spaceId,
                DiagnosticCode: null);
            UpdateStatus(ready);
            RequestSync();
            return new SyncSetupResult(
                SyncSetupStatus.Success,
                spaceId,
                deviceId,
                recoveryPath);
        }
        catch (SyncPipelineException exception)
        {
            return SetupFailure(
                MapRemoteSetupStatus(exception.Category),
                spaceId,
                deviceId,
                exception.DiagnosticCode);
        }
        catch (CryptographicException)
        {
            return SetupFailure(
                SyncSetupStatus.CryptographicFailure,
                spaceId,
                deviceId,
                "cryptographic-failure");
        }
        catch (Exception exception) when (exception is
            DbException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return SetupFailure(
                SyncSetupStatus.PersistenceFailure,
                spaceId,
                deviceId,
                "local-persistence-failure");
        }
        finally
        {
            if (createdRecoveryEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(createdRecoveryEnvelope);
            }

            if (!completed)
            {
                await CleanupFailedSetupAsync(
                        spaceId,
                        keyVersion,
                        keyStored,
                        credentialStored,
                        recoveryStored)
                    .ConfigureAwait(false);
            }

            _singleFlight.Release();
        }
    }

    private async ValueTask<SyncSetupResult> ReconfigureExistingSpaceAsync(
        SyncConfigurationSnapshot configuration,
        SyncSetupRequest request,
        ReadOnlyMemory<byte> password,
        ReadOnlyMemory<byte> recoveryEnvelope,
        ReadOnlyMemory<byte> recoveryCode,
        CancellationToken cancellationToken)
    {
        SyncStatusSnapshot previousStatus = Status;
        UpdateStatus(previousStatus with
        {
            State = SyncServiceState.Synchronizing,
            SpaceId = configuration.SpaceId,
            DiagnosticCode = "configuring",
        });

        try
        {
            SyncMasterKeyOpenResult recovered = await _keyService.RecoverMasterKeyAsync(
                    recoveryEnvelope,
                    recoveryCode,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recovered.Status != SyncKeyOperationStatus.Success || recovered.Key is null)
            {
                return ExistingSetupFailure(
                    configuration,
                    previousStatus,
                    MapKeySetupStatus(recovered.Status),
                    "key-recovery-failed");
            }

            using (recovered.Key)
            {
                SyncMasterKeyOpenResult current = await _keyService.OpenMasterKeyAsync(
                        configuration.SpaceId,
                        configuration.KeyVersion,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (current.Status != SyncKeyOperationStatus.Success || current.Key is null)
                {
                    return ExistingSetupFailure(
                        configuration,
                        previousStatus,
                        MapKeySetupStatus(current.Status),
                        "key-open-failed");
                }

                using (current.Key)
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            recovered.Key.Key.Span,
                            current.Key.Key.Span))
                    {
                        return ExistingSetupFailure(
                            configuration,
                            previousStatus,
                            SyncSetupStatus.CryptographicFailure,
                            "recovery-key-mismatch");
                    }

                    await using ISyncRemoteSession session = _remoteSessionFactory.Create(
                        request.RemoteConfiguration,
                        password);
                    await EnsureAndValidateMetadataAsync(
                            session,
                            configuration.SpaceId,
                            configuration.DeviceId,
                            configuration.KeyVersion,
                            current.Key.Key,
                            createIfMissing: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            SyncCredentialOperationStatus credentialStatus =
                await _credentialService.StoreAsync(
                        configuration.SpaceId,
                        request.RemoteConfiguration,
                        password,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (credentialStatus != SyncCredentialOperationStatus.Success)
            {
                return ExistingSetupFailure(
                    configuration,
                    previousStatus,
                    MapCredentialSetupStatus(credentialStatus),
                    "credential-store-failed");
            }

            UpdateStatus(previousStatus with
            {
                State = configuration.IsEnabled
                    ? SyncServiceState.Idle
                    : SyncServiceState.Disabled,
                SpaceId = configuration.SpaceId,
                DiagnosticCode = null,
            });
            RequestSync();
            return new SyncSetupResult(
                SyncSetupStatus.Success,
                configuration.SpaceId,
                configuration.DeviceId);
        }
        catch (SyncPipelineException exception)
        {
            return ExistingSetupFailure(
                configuration,
                previousStatus,
                MapRemoteSetupStatus(exception.Category),
                exception.DiagnosticCode);
        }
        catch (CryptographicException)
        {
            return ExistingSetupFailure(
                configuration,
                previousStatus,
                SyncSetupStatus.CryptographicFailure,
                "cryptographic-failure");
        }
        catch (Exception exception) when (exception is
            DbException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ExistingSetupFailure(
                configuration,
                previousStatus,
                SyncSetupStatus.PersistenceFailure,
                "local-persistence-failure");
        }
    }

    private SyncSetupResult ExistingSetupFailure(
        SyncConfigurationSnapshot configuration,
        SyncStatusSnapshot previousStatus,
        SyncSetupStatus setupStatus,
        string diagnosticCode)
    {
        UpdateStatus(previousStatus);
        return new SyncSetupResult(
            setupStatus,
            configuration.SpaceId,
            configuration.DeviceId,
            DiagnosticCode: diagnosticCode);
    }

    private async ValueTask CleanupFailedSetupAsync(
        Guid spaceId,
        int keyVersion,
        bool keyStored,
        bool credentialStored,
        bool recoveryStored)
    {
        if (recoveryStored)
        {
            try
            {
                await _recoveryMaterialStore.DeleteAsync(
                        spaceId,
                        keyVersion,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (credentialStored)
        {
            await _credentialService.DeleteAsync(spaceId, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (keyStored)
        {
            await _keyService.DeleteSpaceKeyAsync(
                    spaceId,
                    keyVersion,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _triggers.Reader.WaitToReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (_triggers.Reader.TryRead(out _))
                {
                }

                if (Volatile.Read(ref _paused) == 0)
                {
                    await ExecuteSingleFlightAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PeriodicLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await InitializePollingSettingsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 本地设置读取失败时继续使用构造时的默认间隔，后续打开设置页仍可重试。
            }

            while (await _pollingTimer.WaitForNextTickAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                RequestSync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnHistoryChanged(object? sender, ClipboardHistoryChangedEvent e) => RequestSync();

    private async ValueTask ApplyRemotePollingSettingAsync(
        string value,
        CancellationToken cancellationToken)
    {
        SyncPollingSettings settings = SyncPollingSettingsSerializer.Deserialize(value);
        await _pollingSettingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplyPollingSettings(settings);
            Volatile.Write(ref _pollingSettingsInitialized, 1);
        }
        finally
        {
            _pollingSettingsGate.Release();
        }
    }

    private async ValueTask PublishCurrentPollingSettingsAsync(
        CancellationToken cancellationToken)
    {
        await InitializePollingSettingsAsync(cancellationToken).ConfigureAwait(false);
        await _pollingSettingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _historyService.SetSettingAsync(
                    SyncSettingKeys.PollInterval,
                    SyncPollingSettingsSerializer.Serialize(PollingSettings),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _pollingSettingsGate.Release();
        }
    }

    private void ApplyPollingSettings(SyncPollingSettings settings)
    {
        SyncPollingSettings previous = Interlocked.Exchange(ref _pollingSettings, settings);
        if (previous == settings)
        {
            return;
        }

        _pollingTimer.Period = settings.PollInterval;
        PollingSettingsChanged?.Invoke(this, new SyncPollingSettingsChangedEvent(settings));
    }

    private SyncSetupResult SetupFailure(
        SyncSetupStatus setupStatus,
        Guid spaceId,
        Guid deviceId,
        string diagnosticCode)
    {
        SyncServiceState state = setupStatus switch
        {
            SyncSetupStatus.AuthenticationFailed => SyncServiceState.AuthenticationRequired,
            SyncSetupStatus.PermissionDenied => SyncServiceState.PermissionDenied,
            SyncSetupStatus.KeyStoreFailed or SyncSetupStatus.CryptographicFailure =>
                SyncServiceState.KeyUnavailable,
            _ => SyncServiceState.Error,
        };
        UpdateStatus(new SyncStatusSnapshot(state, spaceId, DiagnosticCode: diagnosticCode));
        return new SyncSetupResult(
            setupStatus,
            spaceId,
            deviceId,
            DiagnosticCode: diagnosticCode);
    }

    private static SyncSetupStatus MapKeySetupStatus(SyncKeyOperationStatus status) =>
        status == SyncKeyOperationStatus.AccessDenied
            ? SyncSetupStatus.PermissionDenied
            : SyncSetupStatus.KeyStoreFailed;

    private static SyncSetupStatus MapCredentialSetupStatus(
        SyncCredentialOperationStatus status) =>
        status == SyncCredentialOperationStatus.AccessDenied
            ? SyncSetupStatus.PermissionDenied
            : SyncSetupStatus.CredentialStoreFailed;

    private static SyncSetupStatus MapRemoteSetupStatus(
        SyncRemoteErrorCategory category) => category switch
        {
            SyncRemoteErrorCategory.Authentication => SyncSetupStatus.AuthenticationFailed,
            SyncRemoteErrorCategory.Permission => SyncSetupStatus.PermissionDenied,
            SyncRemoteErrorCategory.Protocol or SyncRemoteErrorCategory.Certificate or
                SyncRemoteErrorCategory.ResponseTooLarge => SyncSetupStatus.RemoteProtocolError,
            _ => SyncSetupStatus.RemoteUnavailable,
        };

    private void UpdateStatus(SyncStatusSnapshot status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
