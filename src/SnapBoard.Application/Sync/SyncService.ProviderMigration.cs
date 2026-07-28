using System.Data.Common;
using System.Security.Cryptography;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Application.Sync;

public sealed partial class SyncService
{
    public event EventHandler<SyncProviderMigrationSnapshot>? ProviderMigrationChanged;

    public SyncProviderMigrationSnapshot ProviderMigration =>
        Volatile.Read(ref _providerMigration);

    public async ValueTask<SyncProviderMigrationSnapshot> RefreshProviderMigrationAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SyncConfigurationSnapshot? configuration = await _store
                .GetConfigurationAsync(cancellationToken)
                .ConfigureAwait(false);
            if (configuration is null)
            {
                return PublishProviderMigration(new SyncProviderMigrationSnapshot(
                    SyncProviderMigrationState.None));
            }

            SyncProviderMigrationRecord? migration = await _store
                .GetProviderMigrationAsync(configuration.SpaceId, cancellationToken)
                .ConfigureAwait(false);
            if (migration is null)
            {
                SyncCredentialOpenResult active = await _credentialService.OpenAsync(
                        configuration.SpaceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (active.Status != SyncCredentialOperationStatus.Success ||
                    active.Credential is null)
                {
                    return PublishProviderMigration(new SyncProviderMigrationSnapshot(
                        SyncProviderMigrationState.None,
                        SpaceId: configuration.SpaceId,
                        DiagnosticCode: "provider-migration-current-remote-unavailable"));
                }

                using (active.Credential)
                {
                    SyncRemoteConfiguration remote = active.Credential.RemoteConfiguration;
                    return PublishProviderMigration(new SyncProviderMigrationSnapshot(
                        SyncProviderMigrationState.None,
                        SpaceId: configuration.SpaceId,
                        SourceEndpoint: remote.Endpoint.AbsoluteUri,
                        SourceRemoteRoot: remote.RemoteRoot,
                        SourceCertificateSha256Pin: remote.CertificateSha256Pin,
                        SourceAllowInsecureLoopback: remote.AllowInsecureLoopback));
                }
            }

            SyncProviderMigrationIntent? intent = await TryReadLocalMigrationIntentAsync(
                    configuration,
                    migration,
                    cancellationToken)
                .ConfigureAwait(false);
            return await PublishProviderMigrationAsync(migration, intent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderMigrationExpectedException(exception))
        {
            return PublishProviderMigration(ProviderMigration with
            {
                DiagnosticCode = GetProviderMigrationDiagnostic(exception),
            });
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    public async ValueTask<SyncProviderMigrationResult> StartProviderMigrationAsync(
        SyncProviderMigrationRequest request,
        ReadOnlyMemory<byte> targetPassword,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (_providerMigrationSessionFactory is null)
        {
            return ProviderMigrationFailure(
                SyncProviderMigrationStatus.NotSupported,
                "provider-migration-not-supported");
        }

        if (Volatile.Read(ref _paused) != 0)
        {
            return ProviderMigrationFailure(
                SyncProviderMigrationStatus.InvalidState,
                "sync-paused");
        }

        SyncProviderMigrationSnapshot currentMigration = ProviderMigration;
        if (currentMigration.State != SyncProviderMigrationState.None &&
            !IsProviderMigrationTerminal(currentMigration.State))
        {
            return new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.InvalidState,
                currentMigration,
                "provider-migration-already-active");
        }

        try
        {
            SyncStatusSnapshot drained = await DrainRegularSyncBeforeMigrationAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (drained.State != SyncServiceState.Idle)
            {
                return ProviderMigrationFailure(
                    MapSyncStateToProviderMigrationStatus(drained.State),
                    drained.DiagnosticCode ?? "provider-migration-source-sync-failed");
            }

            await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await StartProviderMigrationCoreAsync(
                        request,
                        targetPassword,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _singleFlight.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderMigrationExpectedException(exception))
        {
            return ProviderMigrationFailure(exception);
        }
    }

    public async ValueTask<SyncProviderMigrationResult>
        ProvideProviderMigrationCredentialsAsync(
            Guid planId,
            SyncProviderMigrationRequest request,
            ReadOnlyMemory<byte> targetPassword,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfEqual(planId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(request);
        if (_providerMigrationSessionFactory is null)
        {
            return ProviderMigrationFailure(
                SyncProviderMigrationStatus.NotSupported,
                "provider-migration-not-supported");
        }

        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProvideProviderMigrationCredentialsCoreAsync(
                    planId,
                    request,
                    targetPassword,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderMigrationExpectedException(exception))
        {
            return ProviderMigrationFailure(exception);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    public async ValueTask<SyncProviderMigrationResult> ContinueProviderMigrationAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfEqual(planId, Guid.Empty);
        if (_providerMigrationSessionFactory is null)
        {
            return ProviderMigrationFailure(
                SyncProviderMigrationStatus.NotSupported,
                "provider-migration-not-supported");
        }

        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ContinueProviderMigrationCoreAsync(planId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderMigrationExpectedException(exception))
        {
            return ProviderMigrationFailure(exception);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    public async ValueTask<SyncProviderMigrationResult>
        CancelOrRollbackProviderMigrationAsync(
            Guid planId,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfEqual(planId, Guid.Empty);
        if (_providerMigrationSessionFactory is null)
        {
            return ProviderMigrationFailure(
                SyncProviderMigrationStatus.NotSupported,
                "provider-migration-not-supported");
        }

        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RollbackProviderMigrationCoreAsync(planId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderMigrationExpectedException(exception))
        {
            return ProviderMigrationFailure(exception);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async ValueTask<SyncProviderMigrationResult> StartProviderMigrationCoreAsync(
        SyncProviderMigrationRequest request,
        ReadOnlyMemory<byte> targetPassword,
        CancellationToken cancellationToken)
    {
        SyncConfigurationSnapshot? configuration = await _store
            .GetConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (configuration is null || !configuration.IsEnabled)
        {
            return ProviderMigrationFailure(
                SyncProviderMigrationStatus.NotConfigured,
                "sync-not-configured");
        }

        SyncProviderMigrationRecord? existing = await _store
            .GetProviderMigrationAsync(configuration.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && !IsProviderMigrationTerminal(existing.State))
        {
            SyncProviderMigrationSnapshot snapshot = await PublishProviderMigrationAsync(
                    existing,
                    intent: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.InvalidState,
                snapshot,
                "provider-migration-already-active");
        }

        SyncMasterKeyOpenResult keyResult = await _keyService.OpenMasterKeyAsync(
                configuration.SpaceId,
                configuration.KeyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureMasterKeyAvailable(keyResult);
        SyncCredentialOpenResult activeResult = await _credentialService.OpenAsync(
                configuration.SpaceId,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureCredentialAvailable(activeResult, "provider-migration-source-credential-unavailable");

        using (keyResult.Key!)
        using (activeResult.Credential!)
        {
            SyncCredentialLease active = activeResult.Credential!;
            string sourceFingerprint = ComputeRemoteFingerprint(active.RemoteConfiguration);
            string targetFingerprint = ComputeRemoteFingerprint(request.TargetConfiguration);
            if (string.Equals(sourceFingerprint, targetFingerprint, StringComparison.Ordinal))
            {
                return ProviderMigrationFailure(
                    SyncProviderMigrationStatus.InvalidState,
                    "provider-migration-target-matches-source");
            }

            await using ISyncRemoteProviderMigrationSession sourceMigration =
                _providerMigrationSessionFactory!.CreateProviderMigrationSession(
                    active.RemoteConfiguration,
                    active.Password);
            RemoteProviderMigrationScan remoteScan = await ScanProviderMigrationsAsync(
                    sourceMigration,
                    configuration,
                    keyResult.Key!.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (remoteScan.LatestIntent is not null && !remoteScan.LatestIsTerminal)
            {
                return ProviderMigrationFailure(
                    SyncProviderMigrationStatus.InvalidState,
                    "provider-migration-remote-plan-active");
            }

            await using ISyncRemoteSession sourceSession = _remoteSessionFactory.Create(
                active.RemoteConfiguration,
                active.Password);
            SyncRemoteDeviceListResult deviceList = await sourceSession.ListDevicesAsync(
                    configuration.SpaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRemoteFailure(deviceList.Result, "provider-migration-device-list-failed");
            Guid[] requiredDeviceIds = deviceList.DeviceIds
                .OrderBy(static deviceId => deviceId.ToString("N"), StringComparer.Ordinal)
                .ToArray();
            if (requiredDeviceIds.Length == 0 ||
                !requiredDeviceIds.Contains(configuration.DeviceId))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-device-set-invalid");
            }

            long highestEpoch = Math.Max(existing?.Epoch ?? 0, remoteScan.HighestEpoch);
            if (highestEpoch == long.MaxValue)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-epoch-exhausted");
            }

            long epoch = highestEpoch + 1;
            Guid planId = Guid.NewGuid();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SyncProviderMigrationRecord migration = new(
                planId,
                configuration.SpaceId,
                epoch,
                configuration.DeviceId,
                sourceFingerprint,
                targetFingerprint,
                SyncProviderMigrationState.Draft,
                TotalObjects: 0,
                TotalBytes: 0,
                CompletedObjects: 0,
                CompletedBytes: 0,
                InventorySha256: null,
                DiagnosticCode: null,
                CreatedAtUnixMilliseconds: now,
                UpdatedAtUnixMilliseconds: now);
            await _store.CreateProviderMigrationAsync(
                    migration,
                    requiredDeviceIds,
                    cancellationToken)
                .ConfigureAwait(false);
            await PublishProviderMigrationAsync(migration, intent: null, cancellationToken)
                .ConfigureAwait(false);

            EnsureCredentialOperationSucceeded(
                await _credentialService.StageCurrentForMigrationAsync(
                        configuration.SpaceId,
                        planId,
                        cancellationToken)
                    .ConfigureAwait(false),
                "provider-migration-source-stage-failed");
            EnsureCredentialOperationSucceeded(
                await _credentialService.StageMigrationTargetAsync(
                        configuration.SpaceId,
                        planId,
                        request.TargetConfiguration,
                        targetPassword,
                        cancellationToken)
                    .ConfigureAwait(false),
                "provider-migration-target-stage-failed");

            migration = await SaveProviderMigrationStateAsync(
                    migration,
                    SyncProviderMigrationState.PreflightTarget,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncCredentialOpenResult targetResult = await _credentialService.OpenMigrationAsync(
                    configuration.SpaceId,
                    planId,
                    SyncMigrationCredentialSlot.Target,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureCredentialAvailable(targetResult, "provider-migration-target-credential-unavailable");
            using (targetResult.Credential!)
            await using (ISyncRemoteProviderMigrationSession targetMigration =
                _providerMigrationSessionFactory.CreateProviderMigrationSession(
                    targetResult.Credential!.RemoteConfiguration,
                    targetResult.Credential.Password))
            {
                await EnsureMigrationHierarchiesAsync(
                        sourceMigration,
                        targetMigration,
                        configuration.SpaceId,
                        planId,
                        requiredDeviceIds,
                        cancellationToken)
                    .ConfigureAwait(false);
                SyncProviderMigrationIntent intent = CreateProviderMigrationIntent(
                    migration,
                    active.RemoteConfiguration,
                    request.TargetConfiguration,
                    requiredDeviceIds);
                await PutIntentMarkerAsync(
                        sourceMigration,
                        configuration,
                        intent,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                await PutIntentMarkerAsync(
                        targetMigration,
                        configuration,
                        intent,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);

                migration = await SaveProviderMigrationStateAsync(
                        migration,
                        SyncProviderMigrationState.PreparingDevices,
                        cancellationToken)
                    .ConfigureAwait(false);
                migration = await EnsureLocalProviderMigrationReadyAsync(
                        sourceSession,
                        sourceMigration,
                        targetMigration,
                        configuration,
                        migration,
                        intent,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                SyncProviderMigrationSnapshot snapshot = await PublishProviderMigrationAsync(
                        migration,
                        intent,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new SyncProviderMigrationResult(
                    SyncProviderMigrationStatus.WaitingForDevices,
                    snapshot,
                    requiredDeviceIds.Length == 1
                        ? "provider-migration-ready-to-continue"
                        : "provider-migration-waiting-for-devices");
            }
        }
    }

    private async ValueTask<SyncProviderMigrationResult>
        ProvideProviderMigrationCredentialsCoreAsync(
            Guid planId,
            SyncProviderMigrationRequest request,
            ReadOnlyMemory<byte> targetPassword,
            CancellationToken cancellationToken)
    {
        (SyncConfigurationSnapshot configuration, SyncProviderMigrationRecord migration) =
            await GetProviderMigrationContextAsync(planId, cancellationToken)
                .ConfigureAwait(false);
        if (IsProviderMigrationTerminal(migration.State))
        {
            return new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.InvalidState,
                await PublishProviderMigrationAsync(migration, null, cancellationToken)
                    .ConfigureAwait(false),
                "provider-migration-terminal");
        }

        SyncMasterKeyOpenResult keyResult = await _keyService.OpenMasterKeyAsync(
                configuration.SpaceId,
                configuration.KeyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureMasterKeyAvailable(keyResult);
        EnsureCredentialOperationSucceeded(
            await _credentialService.StageCurrentForMigrationAsync(
                    configuration.SpaceId,
                    planId,
                    cancellationToken)
                .ConfigureAwait(false),
            "provider-migration-source-stage-failed");
        SyncCredentialOpenResult sourceResult = await _credentialService.OpenMigrationAsync(
                configuration.SpaceId,
                planId,
                SyncMigrationCredentialSlot.Source,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureCredentialAvailable(sourceResult, "provider-migration-source-credential-unavailable");

        using (keyResult.Key!)
        using (sourceResult.Credential!)
        await using (ISyncRemoteProviderMigrationSession sourceMigration =
            _providerMigrationSessionFactory!.CreateProviderMigrationSession(
                sourceResult.Credential!.RemoteConfiguration,
                sourceResult.Credential.Password))
        {
            SyncProviderMigrationIntent intent = await ReadRequiredIntentAsync(
                    sourceMigration,
                    configuration,
                    planId,
                    keyResult.Key!.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TargetConfigurationMatchesIntent(request.TargetConfiguration, intent))
            {
                return new SyncProviderMigrationResult(
                    SyncProviderMigrationStatus.InvalidState,
                    await PublishProviderMigrationAsync(migration, intent, cancellationToken)
                        .ConfigureAwait(false),
                    "provider-migration-target-does-not-match-intent");
            }

            EnsureCredentialOperationSucceeded(
                await _credentialService.StageMigrationTargetAsync(
                        configuration.SpaceId,
                        planId,
                        request.TargetConfiguration,
                        targetPassword,
                        cancellationToken)
                    .ConfigureAwait(false),
                "provider-migration-target-stage-failed");
            SyncCredentialOpenResult targetResult = await _credentialService.OpenMigrationAsync(
                    configuration.SpaceId,
                    planId,
                    SyncMigrationCredentialSlot.Target,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureCredentialAvailable(targetResult, "provider-migration-target-credential-unavailable");
            using (targetResult.Credential!)
            await using (ISyncRemoteProviderMigrationSession targetMigration =
                _providerMigrationSessionFactory.CreateProviderMigrationSession(
                    targetResult.Credential!.RemoteConfiguration,
                    targetResult.Credential.Password))
            await using (ISyncRemoteSession sourceSession = _remoteSessionFactory.Create(
                sourceResult.Credential.RemoteConfiguration,
                sourceResult.Credential.Password))
            {
                await EnsureMigrationHierarchiesAsync(
                        sourceMigration,
                        targetMigration,
                        configuration.SpaceId,
                        planId,
                        intent.RequiredDeviceIds,
                        cancellationToken)
                    .ConfigureAwait(false);
                await PutIntentMarkerAsync(
                        targetMigration,
                        configuration,
                        intent,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                migration = await EnsureLocalProviderMigrationReadyAsync(
                        sourceSession,
                        sourceMigration,
                        targetMigration,
                        configuration,
                        migration,
                        intent,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                SyncProviderMigrationSnapshot snapshot = await PublishProviderMigrationAsync(
                        migration,
                        intent,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new SyncProviderMigrationResult(
                    SyncProviderMigrationStatus.WaitingForDevices,
                    snapshot,
                    "provider-migration-waiting-for-devices");
            }
        }
    }

    private async ValueTask<SyncProviderMigrationResult> ContinueProviderMigrationCoreAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        (SyncConfigurationSnapshot configuration, SyncProviderMigrationRecord migration) =
            await GetProviderMigrationContextAsync(planId, cancellationToken)
                .ConfigureAwait(false);
        if (IsProviderMigrationTerminal(migration.State))
        {
            SyncProviderMigrationSnapshot terminal = await PublishProviderMigrationAsync(
                    migration,
                    intent: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SyncProviderMigrationResult(
                migration.State == SyncProviderMigrationState.Completed
                    ? SyncProviderMigrationStatus.Success
                    : SyncProviderMigrationStatus.InvalidState,
                terminal,
                migration.DiagnosticCode);
        }

        SyncMasterKeyOpenResult keyResult = await _keyService.OpenMasterKeyAsync(
                configuration.SpaceId,
                configuration.KeyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureMasterKeyAvailable(keyResult);
        SyncCredentialOpenResult sourceResult = await _credentialService.OpenMigrationAsync(
                configuration.SpaceId,
                planId,
                SyncMigrationCredentialSlot.Source,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureCredentialAvailable(sourceResult, "provider-migration-source-credential-unavailable");
        SyncCredentialOpenResult targetResult = await _credentialService.OpenMigrationAsync(
                configuration.SpaceId,
                planId,
                SyncMigrationCredentialSlot.Target,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetResult.Status == SyncCredentialOperationStatus.NotFound)
        {
            migration = await SaveProviderMigrationStateAsync(
                    migration,
                    SyncProviderMigrationState.TargetCredentialsRequired,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.CredentialStoreFailed,
                await PublishProviderMigrationAsync(migration, null, cancellationToken)
                    .ConfigureAwait(false),
                "provider-migration-target-credentials-required");
        }

        EnsureCredentialAvailable(targetResult, "provider-migration-target-credential-unavailable");
        using (keyResult.Key!)
        using (sourceResult.Credential!)
        using (targetResult.Credential!)
        await using (ISyncRemoteProviderMigrationSession sourceMigration =
            _providerMigrationSessionFactory!.CreateProviderMigrationSession(
                sourceResult.Credential!.RemoteConfiguration,
                sourceResult.Credential.Password))
        await using (ISyncRemoteProviderMigrationSession targetMigration =
            _providerMigrationSessionFactory.CreateProviderMigrationSession(
                targetResult.Credential!.RemoteConfiguration,
                targetResult.Credential.Password))
        await using (ISyncRemoteSession sourceSession = _remoteSessionFactory.Create(
            sourceResult.Credential.RemoteConfiguration,
            sourceResult.Credential.Password))
        {
            SyncProviderMigrationIntent intent = await ReadRequiredIntentAsync(
                    sourceMigration,
                    configuration,
                    planId,
                    keyResult.Key!.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateStoredMigrationAgainstIntent(migration, intent);
            if (!TargetConfigurationMatchesIntent(
                    targetResult.Credential.RemoteConfiguration,
                    intent))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-staged-target-mismatch");
            }

            await EnsureMigrationHierarchiesAsync(
                    sourceMigration,
                    targetMigration,
                    configuration.SpaceId,
                    planId,
                    intent.RequiredDeviceIds,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncProviderMigrationDecision? rollback = await ReadDecisionMarkerAsync(
                    sourceMigration,
                    configuration,
                    intent,
                    SyncProviderMigrationMarkerKind.Rollback,
                    keyResult.Key.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (rollback is not null)
            {
                try
                {
                    await PutDecisionMarkerAsync(
                            targetMigration,
                            configuration,
                            intent,
                            rollback,
                            keyResult.Key.Key,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SyncPipelineException)
                {
                    // 旧端回滚决定保持权威；失败目标不阻止设备恢复源凭据。
                }

                return await ApplyObservedRollbackAsync(
                        configuration,
                        migration,
                        intent,
                        sourceResult.Credential,
                        sourceMigration,
                        targetMigration,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            SyncProviderMigrationDecision? completed = await ReadDecisionMarkerAsync(
                    sourceMigration,
                    configuration,
                    intent,
                    SyncProviderMigrationMarkerKind.Completed,
                    keyResult.Key.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (completed is not null)
            {
                await PutDecisionMarkerAsync(
                        targetMigration,
                        configuration,
                        intent,
                        completed,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await FinalizeObservedCompletionAsync(
                        configuration,
                        migration,
                        intent,
                        targetResult.Credential,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await PutIntentMarkerAsync(
                    targetMigration,
                    configuration,
                    intent,
                    keyResult.Key.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            migration = await EnsureLocalProviderMigrationReadyAsync(
                    sourceSession,
                    sourceMigration,
                    targetMigration,
                    configuration,
                    migration,
                    intent,
                    keyResult.Key.Key,
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyDictionary<Guid, SyncProviderMigrationDeviceMarker> readyMarkers =
                await ReadDeviceMarkersAsync(
                        sourceMigration,
                        configuration,
                        intent,
                        SyncProviderMigrationMarkerKind.Ready,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
            await UpdateDeviceRecordsFromMarkersAsync(
                    migration.PlanId,
                    readyMarkers,
                    SyncProviderMigrationDeviceState.Ready,
                    cancellationToken)
                .ConfigureAwait(false);
            if (readyMarkers.Count != intent.RequiredDeviceIds.Length)
            {
                migration = await SaveProviderMigrationStateAsync(
                        migration,
                        SyncProviderMigrationState.WaitingForDeviceAcks,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new SyncProviderMigrationResult(
                    SyncProviderMigrationStatus.WaitingForDevices,
                    await PublishProviderMigrationAsync(migration, intent, cancellationToken)
                        .ConfigureAwait(false),
                    "provider-migration-waiting-for-devices");
            }

            SyncProviderMigrationDecision? commit = await ReadDecisionMarkerAsync(
                    sourceMigration,
                    configuration,
                    intent,
                    SyncProviderMigrationMarkerKind.Commit,
                    keyResult.Key.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (commit is null)
            {
                if (configuration.DeviceId != intent.InitiatorDeviceId)
                {
                    return new SyncProviderMigrationResult(
                        SyncProviderMigrationStatus.WaitingForDevices,
                        await PublishProviderMigrationAsync(migration, intent, cancellationToken)
                            .ConfigureAwait(false),
                        "provider-migration-waiting-for-coordinator");
                }

                SyncProviderMigrationDecision freeze = CreateDecision(
                    intent,
                    SyncProviderMigrationMarkerKind.Freeze,
                    migration);
                await PutDecisionMarkerAsync(
                        sourceMigration,
                        configuration,
                        intent,
                        freeze,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                await PutDecisionMarkerAsync(
                        targetMigration,
                        configuration,
                        intent,
                        freeze,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                migration = await SaveProviderMigrationStateAsync(
                        migration,
                        SyncProviderMigrationState.Frozen,
                        cancellationToken)
                    .ConfigureAwait(false);
                migration = await MirrorAndVerifyProviderMigrationAsync(
                        sourceMigration,
                        targetMigration,
                        configuration,
                        migration,
                        intent,
                        readyMarkers,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                commit = CreateDecision(
                    intent,
                    SyncProviderMigrationMarkerKind.Commit,
                    migration);
                await PutDecisionMarkerAsync(
                        sourceMigration,
                        configuration,
                        intent,
                        commit,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                await PutDecisionMarkerAsync(
                        targetMigration,
                        configuration,
                        intent,
                        commit,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                migration = await SaveProviderMigrationStateAsync(
                        migration,
                        SyncProviderMigrationState.Committing,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                ValidateCommitAgainstMigration(commit, migration);
                await PutDecisionMarkerAsync(
                        targetMigration,
                        configuration,
                        intent,
                        commit,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (migration.InventorySha256 is null)
                {
                    migration = migration with
                    {
                        TotalObjects = commit.ObjectCount,
                        TotalBytes = commit.TotalBytes,
                        CompletedObjects = commit.ObjectCount,
                        CompletedBytes = commit.TotalBytes,
                        InventorySha256 = commit.InventorySha256,
                        UpdatedAtUnixMilliseconds =
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    await _store.SaveProviderMigrationAsync(migration, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            IReadOnlyDictionary<Guid, SyncProviderMigrationDeviceMarker> committedMarkers =
                await ReadDeviceMarkersAsync(
                        sourceMigration,
                        configuration,
                        intent,
                        SyncProviderMigrationMarkerKind.Committed,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!committedMarkers.ContainsKey(configuration.DeviceId))
            {
                EnsureCredentialOperationSucceeded(
                    await _credentialService.CommitMigrationTargetAsync(
                            configuration.SpaceId,
                            planId,
                            cancellationToken)
                        .ConfigureAwait(false),
                    "provider-migration-credential-commit-failed");
                await ValidateCommittedTargetAsync(
                        configuration,
                        targetResult.Credential,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                SyncProviderMigrationDeviceRecord localRecord = await GetLocalMigrationDeviceAsync(
                        planId,
                        configuration.DeviceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                SyncProviderMigrationWatermark committedWatermark = new(
                    localRecord.HighestLocalSequence,
                    localRecord.HighestUploadedSequence,
                    []);
                SyncProviderMigrationDeviceMarker committed = CreateDeviceMarker(
                    intent,
                    SyncProviderMigrationMarkerKind.Committed,
                    configuration.DeviceId,
                    committedWatermark);
                await PutDeviceMarkerAsync(
                        sourceMigration,
                        configuration,
                        intent,
                        committed,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                await PutDeviceMarkerAsync(
                        targetMigration,
                        configuration,
                        intent,
                        committed,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                await SaveProviderMigrationDeviceAsync(
                        planId,
                        configuration.DeviceId,
                        SyncProviderMigrationDeviceState.Committed,
                        localRecord.HighestLocalSequence,
                        localRecord.HighestUploadedSequence,
                        diagnosticCode: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            migration = await SaveProviderMigrationStateAsync(
                    migration,
                    SyncProviderMigrationState.WaitingForDeviceCommits,
                    cancellationToken)
                .ConfigureAwait(false);
            committedMarkers = await ReadDeviceMarkersAsync(
                    sourceMigration,
                    configuration,
                    intent,
                    SyncProviderMigrationMarkerKind.Committed,
                    keyResult.Key.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            await UpdateDeviceRecordsFromMarkersAsync(
                    planId,
                    committedMarkers,
                    SyncProviderMigrationDeviceState.Committed,
                    cancellationToken)
                .ConfigureAwait(false);
            if (committedMarkers.Count != intent.RequiredDeviceIds.Length)
            {
                return new SyncProviderMigrationResult(
                    SyncProviderMigrationStatus.WaitingForDevices,
                    await PublishProviderMigrationAsync(migration, intent, cancellationToken)
                        .ConfigureAwait(false),
                    "provider-migration-waiting-for-device-commits");
            }

            if (configuration.DeviceId == intent.InitiatorDeviceId)
            {
                SyncProviderMigrationDecision completion = CreateDecision(
                    intent,
                    SyncProviderMigrationMarkerKind.Completed,
                    migration);
                await PutDecisionMarkerAsync(
                        sourceMigration,
                        configuration,
                        intent,
                        completion,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                await PutDecisionMarkerAsync(
                        targetMigration,
                        configuration,
                        intent,
                        completion,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                completed = await ReadDecisionMarkerAsync(
                        sourceMigration,
                        configuration,
                        intent,
                        SyncProviderMigrationMarkerKind.Completed,
                        keyResult.Key.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (completed is null)
                {
                    return new SyncProviderMigrationResult(
                        SyncProviderMigrationStatus.WaitingForDevices,
                        await PublishProviderMigrationAsync(migration, intent, cancellationToken)
                            .ConfigureAwait(false),
                        "provider-migration-waiting-for-completion");
                }
            }

            return await CompleteLocalProviderMigrationAsync(
                    configuration,
                    migration,
                    intent,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<SyncProviderMigrationResult> RollbackProviderMigrationCoreAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        (SyncConfigurationSnapshot configuration, SyncProviderMigrationRecord migration) =
            await GetProviderMigrationContextAsync(planId, cancellationToken)
                .ConfigureAwait(false);
        if (migration.State is SyncProviderMigrationState.Completed or
            SyncProviderMigrationState.RolledBack)
        {
            return new SyncProviderMigrationResult(
                SyncProviderMigrationStatus.InvalidState,
                await PublishProviderMigrationAsync(migration, null, cancellationToken)
                    .ConfigureAwait(false),
                "provider-migration-terminal");
        }

        migration = await SaveProviderMigrationStateAsync(
                migration,
                SyncProviderMigrationState.RollingBack,
                cancellationToken)
            .ConfigureAwait(false);
        SyncMasterKeyOpenResult keyResult = await _keyService.OpenMasterKeyAsync(
                configuration.SpaceId,
                configuration.KeyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureMasterKeyAvailable(keyResult);
        SyncCredentialOpenResult sourceResult = await _credentialService.OpenMigrationAsync(
                configuration.SpaceId,
                planId,
                SyncMigrationCredentialSlot.Source,
                cancellationToken)
            .ConfigureAwait(false);
        if (sourceResult.Status == SyncCredentialOperationStatus.NotFound)
        {
            sourceResult = await _credentialService.OpenAsync(
                    configuration.SpaceId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        EnsureCredentialAvailable(sourceResult, "provider-migration-source-credential-unavailable");
        SyncProviderMigrationIntent? observedIntent = null;
        bool allDevicesRolledBack = true;
        using (keyResult.Key!)
        using (sourceResult.Credential!)
        await using (ISyncRemoteProviderMigrationSession sourceMigration =
            _providerMigrationSessionFactory!.CreateProviderMigrationSession(
                sourceResult.Credential!.RemoteConfiguration,
                sourceResult.Credential.Password))
        {
            SyncProviderMigrationIntent? intent = await ReadProviderMigrationIntentAsync(
                    sourceMigration,
                    configuration,
                    planId,
                    keyResult.Key!.Key,
                    allowNotFound: true,
                    cancellationToken)
                .ConfigureAwait(false);
            observedIntent = intent;
            SyncCredentialOpenResult targetResult = await _credentialService.OpenMigrationAsync(
                    configuration.SpaceId,
                    planId,
                    SyncMigrationCredentialSlot.Target,
                    cancellationToken)
                .ConfigureAwait(false);
            using (targetResult.Credential)
            await using (ISyncRemoteProviderMigrationSession? targetMigration =
                targetResult.Credential is null
                    ? null
                    : _providerMigrationSessionFactory.CreateProviderMigrationSession(
                        targetResult.Credential.RemoteConfiguration,
                        targetResult.Credential.Password))
            {
                if (intent is not null)
                {
                    SyncProviderMigrationDecision rollback = CreateDecision(
                        intent,
                        SyncProviderMigrationMarkerKind.Rollback,
                        migration);
                    await PutDecisionMarkerAsync(
                            sourceMigration,
                            configuration,
                            intent,
                            rollback,
                            keyResult.Key.Key,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (targetMigration is not null)
                    {
                        try
                        {
                            await PutDecisionMarkerAsync(
                                    targetMigration,
                                    configuration,
                                    intent,
                                    rollback,
                                    keyResult.Key.Key,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (SyncPipelineException)
                        {
                            // 旧远端上的全局回滚标记仍是权威，失败目标不阻止恢复旧端。
                        }
                    }
                }

                SyncCredentialOperationStatus restored = await _credentialService
                    .RollbackMigrationSourceAsync(
                        configuration.SpaceId,
                        planId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (restored != SyncCredentialOperationStatus.NotFound)
                {
                    EnsureCredentialOperationSucceeded(
                        restored,
                        "provider-migration-source-restore-failed");
                }

                await using (ISyncRemoteSession sourceSession = _remoteSessionFactory.Create(
                    sourceResult.Credential.RemoteConfiguration,
                    sourceResult.Credential.Password))
                {
                    await EnsureAndValidateMetadataAsync(
                            sourceSession,
                            configuration.SpaceId,
                            configuration.DeviceId,
                            configuration.KeyVersion,
                            keyResult.Key.Key,
                            createIfMissing: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                SyncProviderMigrationDeviceRecord local = await GetLocalMigrationDeviceAsync(
                        planId,
                        configuration.DeviceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (intent is not null)
                {
                    SyncProviderMigrationWatermark watermark = new(
                        local.HighestLocalSequence,
                        local.HighestUploadedSequence,
                        []);
                    SyncProviderMigrationDeviceMarker rolledBack = CreateDeviceMarker(
                        intent,
                        SyncProviderMigrationMarkerKind.RolledBack,
                        configuration.DeviceId,
                        watermark);
                    await PutDeviceMarkerAsync(
                            sourceMigration,
                            configuration,
                            intent,
                            rolledBack,
                            keyResult.Key.Key,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (targetMigration is not null)
                    {
                        try
                        {
                            await PutDeviceMarkerAsync(
                                    targetMigration,
                                    configuration,
                                    intent,
                                    rolledBack,
                                    keyResult.Key.Key,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (SyncPipelineException)
                        {
                        }
                    }
                }

                await SaveProviderMigrationDeviceAsync(
                        planId,
                        configuration.DeviceId,
                        SyncProviderMigrationDeviceState.RolledBack,
                        local.HighestLocalSequence,
                        local.HighestUploadedSequence,
                        diagnosticCode: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (intent is not null)
                {
                    IReadOnlyDictionary<Guid, SyncProviderMigrationDeviceMarker> rolledBackMarkers =
                        await ReadDeviceMarkersAsync(
                                sourceMigration,
                                configuration,
                                intent,
                                SyncProviderMigrationMarkerKind.RolledBack,
                                keyResult.Key.Key,
                                cancellationToken)
                            .ConfigureAwait(false);
                    await UpdateDeviceRecordsFromMarkersAsync(
                            planId,
                            rolledBackMarkers,
                            SyncProviderMigrationDeviceState.RolledBack,
                            cancellationToken)
                        .ConfigureAwait(false);
                    allDevicesRolledBack =
                        rolledBackMarkers.Count == intent.RequiredDeviceIds.Length;
                }

                migration = await SaveProviderMigrationStateAsync(
                        migration,
                        allDevicesRolledBack
                            ? SyncProviderMigrationState.RolledBack
                            : SyncProviderMigrationState.RollingBack,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        SyncCredentialOperationStatus targetDeleted = await _credentialService
            .DeleteMigrationSlotAsync(
                configuration.SpaceId,
                planId,
                SyncMigrationCredentialSlot.Target,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (targetDeleted != SyncCredentialOperationStatus.NotFound)
        {
            EnsureCredentialOperationSucceeded(
                targetDeleted,
                "provider-migration-target-cleanup-failed");
        }

        if (allDevicesRolledBack)
        {
            RequestSync();
        }

        SyncProviderMigrationSnapshot snapshot = await PublishProviderMigrationAsync(
                migration,
                observedIntent,
                cancellationToken)
            .ConfigureAwait(false);
        return new SyncProviderMigrationResult(
            allDevicesRolledBack
                ? SyncProviderMigrationStatus.Success
                : SyncProviderMigrationStatus.WaitingForDevices,
            snapshot,
            allDevicesRolledBack
                ? "provider-migration-rolled-back"
                : "provider-migration-waiting-for-rollbacks");
    }

    private async ValueTask EnsureProviderMigrationAllowsUploadAsync(
        SyncConfigurationSnapshot configuration,
        ReadOnlyMemory<byte> masterKey,
        SyncCredentialLease activeCredential,
        CancellationToken cancellationToken)
    {
        if (_providerMigrationSessionFactory is null)
        {
            return;
        }

        SyncProviderMigrationRecord? local = await _store.GetProviderMigrationAsync(
                configuration.SpaceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (local?.State == SyncProviderMigrationState.Failed)
        {
            await PublishProviderMigrationAsync(local, null, cancellationToken)
                .ConfigureAwait(false);
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-authority-unresolved");
        }

        if (local is not null && !IsProviderMigrationTerminal(local.State))
        {
            await PublishProviderMigrationAsync(local, null, cancellationToken)
                .ConfigureAwait(false);
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                local.State == SyncProviderMigrationState.TargetCredentialsRequired
                    ? "provider-migration-target-credentials-required"
                    : "provider-migration-write-frozen");
        }

        await using ISyncRemoteProviderMigrationSession session =
            _providerMigrationSessionFactory.CreateProviderMigrationSession(
                activeCredential.RemoteConfiguration,
                activeCredential.Password);
        RemoteProviderMigrationScan scan = await ScanProviderMigrationsAsync(
                session,
                configuration,
                masterKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (scan.LatestIntent is null || scan.LatestRolledBack ||
            local is not null && scan.LatestIntent.Epoch <= local.Epoch)
        {
            return;
        }

        SyncProviderMigrationIntent intent = scan.LatestIntent;
        if (!intent.RequiredDeviceIds.Contains(configuration.DeviceId) ||
            !string.Equals(
                ComputeRemoteFingerprint(activeCredential.RemoteConfiguration),
                intent.SourceRemoteFingerprint,
                StringComparison.Ordinal))
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-intent-source-mismatch");
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SyncProviderMigrationRecord migration = new(
            intent.PlanId,
            intent.SpaceId,
            intent.Epoch,
            intent.InitiatorDeviceId,
            intent.SourceRemoteFingerprint,
            intent.TargetRemoteFingerprint,
            SyncProviderMigrationState.TargetCredentialsRequired,
            TotalObjects: 0,
            TotalBytes: 0,
            CompletedObjects: 0,
            CompletedBytes: 0,
            InventorySha256: null,
            DiagnosticCode: "provider-migration-target-credentials-required",
            CreatedAtUnixMilliseconds: now,
            UpdatedAtUnixMilliseconds: now);
        await _store.CreateProviderMigrationAsync(
                migration,
                intent.RequiredDeviceIds,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureCredentialOperationSucceeded(
            await _credentialService.StageCurrentForMigrationAsync(
                    configuration.SpaceId,
                    intent.PlanId,
                    cancellationToken)
                .ConfigureAwait(false),
            "provider-migration-source-stage-failed");
        await SaveProviderMigrationDeviceAsync(
                intent.PlanId,
                configuration.DeviceId,
                SyncProviderMigrationDeviceState.TargetCredentialsRequired,
                highestLocalSequence: 0,
                highestUploadedSequence: 0,
                "provider-migration-target-credentials-required",
                cancellationToken)
            .ConfigureAwait(false);
        await PublishProviderMigrationAsync(migration, intent, cancellationToken)
            .ConfigureAwait(false);
        throw new SyncPipelineException(
            SyncRemoteErrorCategory.Protocol,
            "provider-migration-target-credentials-required");
    }

    private async ValueTask<SyncStatusSnapshot> DrainRegularSyncBeforeMigrationAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            SyncStatusSnapshot status = await SynchronizeNowAsync(cancellationToken)
                .ConfigureAwait(false);
            if (status.State != SyncServiceState.Idle ||
                status.UploadedEvents < _options.MaximumUploadBatch &&
                status.DownloadedEvents < _options.MaximumDownloadBatchPerDevice)
            {
                return status;
            }
        }
    }

    private async ValueTask<SyncProviderMigrationRecord>
        EnsureLocalProviderMigrationReadyAsync(
            ISyncRemoteSession sourceSession,
            ISyncRemoteProviderMigrationSession sourceMigration,
            ISyncRemoteProviderMigrationSession targetMigration,
            SyncConfigurationSnapshot configuration,
            SyncProviderMigrationRecord migration,
            SyncProviderMigrationIntent intent,
            ReadOnlyMemory<byte> masterKey,
            CancellationToken cancellationToken)
    {
        SyncProviderMigrationDeviceMarker? ready = await ReadDeviceMarkerAsync(
                sourceMigration,
                configuration,
                intent,
                SyncProviderMigrationMarkerKind.Ready,
                configuration.DeviceId,
                masterKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (ready is null)
        {
            SyncProviderMigrationDecision? freeze = await ReadDecisionMarkerAsync(
                    sourceMigration,
                    configuration,
                    intent,
                    SyncProviderMigrationMarkerKind.Freeze,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncProviderMigrationDecision? commit = await ReadDecisionMarkerAsync(
                    sourceMigration,
                    configuration,
                    intent,
                    SyncProviderMigrationMarkerKind.Commit,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (freeze is not null || commit is not null)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-local-ready-missing-after-freeze");
            }

            await DrainSourceWithinMigrationAsync(
                    sourceSession,
                    configuration,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncProviderMigrationWatermark watermark = await _store
                .CaptureProviderMigrationWatermarkAsync(
                    configuration.SpaceId,
                    configuration.DeviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (watermark.HighestUploadedSequence != watermark.HighestLocalSequence)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Transient,
                    "provider-migration-outbox-not-drained");
            }

            ready = CreateDeviceMarker(
                intent,
                SyncProviderMigrationMarkerKind.Ready,
                configuration.DeviceId,
                watermark);
            await PutDeviceMarkerAsync(
                    sourceMigration,
                    configuration,
                    intent,
                    ready,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await PutDeviceMarkerAsync(
                targetMigration,
                configuration,
                intent,
                ready,
                masterKey,
                cancellationToken)
            .ConfigureAwait(false);
        await SaveProviderMigrationDeviceAsync(
                migration.PlanId,
                configuration.DeviceId,
                SyncProviderMigrationDeviceState.Ready,
                ready.HighestLocalSequence,
                ready.HighestUploadedSequence,
                diagnosticCode: null,
                cancellationToken)
            .ConfigureAwait(false);
        return await SaveProviderMigrationStateAsync(
                migration,
                SyncProviderMigrationState.WaitingForDeviceAcks,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask DrainSourceWithinMigrationAsync(
        ISyncRemoteSession sourceSession,
        SyncConfigurationSnapshot configuration,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        await EnsureAndValidateMetadataAsync(
                sourceSession,
                configuration.SpaceId,
                configuration.DeviceId,
                configuration.KeyVersion,
                masterKey,
                createIfMissing: false,
                cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            int uploaded = await UploadAsync(
                    sourceSession,
                    configuration,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            int downloaded = await DownloadAsync(
                    sourceSession,
                    configuration,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (uploaded < _options.MaximumUploadBatch &&
                downloaded < _options.MaximumDownloadBatchPerDevice)
            {
                return;
            }
        }
    }

    private async ValueTask ValidateCommittedTargetAsync(
        SyncConfigurationSnapshot configuration,
        SyncCredentialLease targetCredential,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        await using ISyncRemoteSession targetSession = _remoteSessionFactory.Create(
            targetCredential.RemoteConfiguration,
            targetCredential.Password);
        await EnsureAndValidateMetadataAsync(
                targetSession,
                configuration.SpaceId,
                configuration.DeviceId,
                configuration.KeyVersion,
                masterKey,
                createIfMissing: false,
                cancellationToken)
            .ConfigureAwait(false);
        while (await DownloadAsync(
                targetSession,
                configuration,
                masterKey,
                cancellationToken)
            .ConfigureAwait(false) >= _options.MaximumDownloadBatchPerDevice)
        {
        }
    }

    private async ValueTask<SyncProviderMigrationResult> CompleteLocalProviderMigrationAsync(
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationRecord migration,
        SyncProviderMigrationIntent intent,
        CancellationToken cancellationToken)
    {
        migration = await SaveProviderMigrationStateAsync(
                migration,
                SyncProviderMigrationState.Completed,
                cancellationToken)
            .ConfigureAwait(false);
        _ = await _credentialService.DeleteMigrationSlotAsync(
                configuration.SpaceId,
                migration.PlanId,
                SyncMigrationCredentialSlot.Target,
                CancellationToken.None)
            .ConfigureAwait(false);
        RequestSync();
        SyncProviderMigrationSnapshot snapshot = await PublishProviderMigrationAsync(
                migration,
                intent,
                cancellationToken,
                oldRemoteRetained: true)
            .ConfigureAwait(false);
        return new SyncProviderMigrationResult(
            SyncProviderMigrationStatus.Success,
            snapshot,
            "provider-migration-completed-old-remote-retained");
    }

    private async ValueTask<SyncProviderMigrationResult> FinalizeObservedCompletionAsync(
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationRecord migration,
        SyncProviderMigrationIntent intent,
        SyncCredentialLease targetCredential,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        EnsureCredentialOperationSucceeded(
            await _credentialService.CommitMigrationTargetAsync(
                    configuration.SpaceId,
                    migration.PlanId,
                    cancellationToken)
                .ConfigureAwait(false),
            "provider-migration-credential-commit-failed");
        await ValidateCommittedTargetAsync(
                configuration,
                targetCredential,
                masterKey,
                cancellationToken)
            .ConfigureAwait(false);
        return await CompleteLocalProviderMigrationAsync(
                configuration,
                migration,
                intent,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<SyncProviderMigrationResult> ApplyObservedRollbackAsync(
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationRecord migration,
        SyncProviderMigrationIntent intent,
        SyncCredentialLease sourceCredential,
        ISyncRemoteProviderMigrationSession sourceMigration,
        ISyncRemoteProviderMigrationSession targetMigration,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        migration = await SaveProviderMigrationStateAsync(
                migration,
                SyncProviderMigrationState.RollingBack,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureCredentialOperationSucceeded(
            await _credentialService.RollbackMigrationSourceAsync(
                    configuration.SpaceId,
                    migration.PlanId,
                    cancellationToken)
                .ConfigureAwait(false),
            "provider-migration-source-restore-failed");
        await using (ISyncRemoteSession sourceSession = _remoteSessionFactory.Create(
            sourceCredential.RemoteConfiguration,
            sourceCredential.Password))
        {
            await EnsureAndValidateMetadataAsync(
                    sourceSession,
                    configuration.SpaceId,
                    configuration.DeviceId,
                    configuration.KeyVersion,
                    masterKey,
                    createIfMissing: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        SyncProviderMigrationDeviceRecord local = await GetLocalMigrationDeviceAsync(
                migration.PlanId,
                configuration.DeviceId,
                cancellationToken)
            .ConfigureAwait(false);
        SyncProviderMigrationDeviceMarker rolledBack = CreateDeviceMarker(
            intent,
            SyncProviderMigrationMarkerKind.RolledBack,
            configuration.DeviceId,
            new SyncProviderMigrationWatermark(
                local.HighestLocalSequence,
                local.HighestUploadedSequence,
                []));
        await PutDeviceMarkerAsync(
                sourceMigration,
                configuration,
                intent,
                rolledBack,
                masterKey,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await PutDeviceMarkerAsync(
                    targetMigration,
                    configuration,
                    intent,
                    rolledBack,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SyncPipelineException)
        {
        }

        await SaveProviderMigrationDeviceAsync(
                migration.PlanId,
                configuration.DeviceId,
                SyncProviderMigrationDeviceState.RolledBack,
                local.HighestLocalSequence,
                local.HighestUploadedSequence,
                diagnosticCode: null,
                cancellationToken)
            .ConfigureAwait(false);
        SyncCredentialOperationStatus targetDeleted = await _credentialService
            .DeleteMigrationSlotAsync(
                configuration.SpaceId,
                migration.PlanId,
                SyncMigrationCredentialSlot.Target,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (targetDeleted != SyncCredentialOperationStatus.NotFound)
        {
            EnsureCredentialOperationSucceeded(
                targetDeleted,
                "provider-migration-target-cleanup-failed");
        }

        migration = await SaveProviderMigrationStateAsync(
                migration,
                SyncProviderMigrationState.RolledBack,
                cancellationToken)
            .ConfigureAwait(false);
        RequestSync();
        return new SyncProviderMigrationResult(
            SyncProviderMigrationStatus.Success,
            await PublishProviderMigrationAsync(migration, intent, cancellationToken)
                .ConfigureAwait(false),
            "provider-migration-rolled-back");
    }

    private async ValueTask<(SyncConfigurationSnapshot Configuration,
        SyncProviderMigrationRecord Migration)> GetProviderMigrationContextAsync(
            Guid planId,
            CancellationToken cancellationToken)
    {
        SyncConfigurationSnapshot? configuration = await _store
            .GetConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (configuration is null)
        {
            throw new InvalidOperationException("The sync space is not configured.");
        }

        SyncProviderMigrationRecord? migration = await _store
            .GetProviderMigrationAsync(configuration.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        if (migration is null || migration.PlanId != planId)
        {
            throw new InvalidOperationException("The provider migration is unavailable.");
        }

        return (configuration, migration);
    }

    private async ValueTask<SyncProviderMigrationRecord> SaveProviderMigrationStateAsync(
        SyncProviderMigrationRecord migration,
        SyncProviderMigrationState state,
        CancellationToken cancellationToken)
    {
        SyncProviderMigrationRecord updated = migration with
        {
            State = state,
            DiagnosticCode = null,
            UpdatedAtUnixMilliseconds = Math.Max(
                migration.UpdatedAtUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };
        await _store.SaveProviderMigrationAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        await PublishProviderMigrationAsync(updated, intent: null, cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }

    private async ValueTask SaveProviderMigrationDeviceAsync(
        Guid planId,
        Guid deviceId,
        SyncProviderMigrationDeviceState state,
        long highestLocalSequence,
        long highestUploadedSequence,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        await _store.SaveProviderMigrationDeviceAsync(
                new SyncProviderMigrationDeviceRecord(
                    planId,
                    deviceId,
                    state,
                    highestLocalSequence,
                    highestUploadedSequence,
                    diagnosticCode,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<SyncProviderMigrationDeviceRecord> GetLocalMigrationDeviceAsync(
        Guid planId,
        Guid localDeviceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SyncProviderMigrationDeviceRecord> devices = await _store
            .GetProviderMigrationDevicesAsync(planId, cancellationToken)
            .ConfigureAwait(false);
        return devices.Single(device => device.DeviceId == localDeviceId);
    }

    private async ValueTask UpdateDeviceRecordsFromMarkersAsync(
        Guid planId,
        IReadOnlyDictionary<Guid, SyncProviderMigrationDeviceMarker> markers,
        SyncProviderMigrationDeviceState state,
        CancellationToken cancellationToken)
    {
        foreach (SyncProviderMigrationDeviceMarker marker in markers.Values)
        {
            await SaveProviderMigrationDeviceAsync(
                    planId,
                    marker.DeviceId,
                    state,
                    marker.HighestLocalSequence,
                    marker.HighestUploadedSequence,
                    marker.DiagnosticCode,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<SyncProviderMigrationSnapshot> PublishProviderMigrationAsync(
        SyncProviderMigrationRecord migration,
        SyncProviderMigrationIntent? intent,
        CancellationToken cancellationToken,
        bool oldRemoteRetained = false)
    {
        IReadOnlyList<SyncProviderMigrationDeviceRecord> devices = await _store
            .GetProviderMigrationDevicesAsync(migration.PlanId, cancellationToken)
            .ConfigureAwait(false);
        SyncProviderMigrationSnapshot snapshot = new(
            migration.State,
            migration.PlanId,
            migration.SpaceId,
            migration.Epoch,
            migration.InitiatorDeviceId,
            intent?.SourceEndpoint,
            intent?.SourceRemoteRoot,
            intent?.TargetEndpoint,
            intent?.TargetRemoteRoot,
            devices.Select(static device => new SyncProviderMigrationDeviceSnapshot(
                    device.DeviceId,
                    device.State,
                    device.HighestLocalSequence,
                    device.HighestUploadedSequence,
                    device.DiagnosticCode))
                .ToArray(),
            migration.TotalObjects,
            migration.TotalBytes,
            migration.CompletedObjects,
            migration.CompletedBytes,
            oldRemoteRetained || migration.State == SyncProviderMigrationState.Completed,
            migration.DiagnosticCode,
            intent?.SourceCertificateSha256Pin,
            intent?.SourceAllowInsecureLoopback ?? false,
            intent?.TargetCertificateSha256Pin,
            intent?.TargetAllowInsecureLoopback ?? false);
        return PublishProviderMigration(snapshot);
    }

    private SyncProviderMigrationSnapshot PublishProviderMigration(
        SyncProviderMigrationSnapshot snapshot)
    {
        Volatile.Write(ref _providerMigration, snapshot);
        ProviderMigrationChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    private SyncProviderMigrationResult ProviderMigrationFailure(Exception exception) =>
        ProviderMigrationFailure(
            MapProviderMigrationStatus(exception),
            GetProviderMigrationDiagnostic(exception));

    private SyncProviderMigrationResult ProviderMigrationFailure(
        SyncProviderMigrationStatus status,
        string diagnosticCode)
    {
        SyncProviderMigrationSnapshot snapshot = PublishProviderMigration(ProviderMigration with
        {
            DiagnosticCode = diagnosticCode,
        });
        return new SyncProviderMigrationResult(status, snapshot, diagnosticCode);
    }

    private static bool IsProviderMigrationExpectedException(Exception exception) => exception is
        SyncPipelineException or CryptographicException or InvalidDataException or
        System.Text.Json.JsonException or DbException or IOException or
        UnauthorizedAccessException or InvalidOperationException;

    private static SyncProviderMigrationStatus MapProviderMigrationStatus(Exception exception) =>
        exception switch
        {
            SyncPipelineException pipeline => MapRemoteProviderMigrationStatus(pipeline.Category),
            CryptographicException => SyncProviderMigrationStatus.CryptographicFailure,
            InvalidDataException or System.Text.Json.JsonException =>
                SyncProviderMigrationStatus.RemoteProtocolError,
            UnauthorizedAccessException => SyncProviderMigrationStatus.PermissionDenied,
            _ => SyncProviderMigrationStatus.PersistenceFailure,
        };

    private static SyncProviderMigrationStatus MapRemoteProviderMigrationStatus(
        SyncRemoteErrorCategory category) => category switch
        {
            SyncRemoteErrorCategory.Authentication =>
                SyncProviderMigrationStatus.CredentialStoreFailed,
            SyncRemoteErrorCategory.Permission => SyncProviderMigrationStatus.PermissionDenied,
            SyncRemoteErrorCategory.Protocol or SyncRemoteErrorCategory.Certificate or
                SyncRemoteErrorCategory.ResponseTooLarge or SyncRemoteErrorCategory.AlreadyExists =>
                SyncProviderMigrationStatus.RemoteProtocolError,
            _ => SyncProviderMigrationStatus.RemoteUnavailable,
        };

    private static SyncProviderMigrationStatus MapSyncStateToProviderMigrationStatus(
        SyncServiceState state) => state switch
        {
            SyncServiceState.NotConfigured or SyncServiceState.Disabled =>
                SyncProviderMigrationStatus.NotConfigured,
            SyncServiceState.PermissionDenied => SyncProviderMigrationStatus.PermissionDenied,
            SyncServiceState.AuthenticationRequired or SyncServiceState.KeyUnavailable =>
                SyncProviderMigrationStatus.CredentialStoreFailed,
            _ => SyncProviderMigrationStatus.RemoteUnavailable,
        };

    private static string GetProviderMigrationDiagnostic(Exception exception) =>
        exception is SyncPipelineException pipeline
            ? pipeline.DiagnosticCode
            : exception switch
            {
                CryptographicException => "provider-migration-cryptographic-failure",
                InvalidDataException or System.Text.Json.JsonException =>
                    "provider-migration-protocol-invalid",
                UnauthorizedAccessException => "provider-migration-permission-denied",
                _ => "provider-migration-persistence-failure",
            };

    private static bool IsProviderMigrationTerminal(SyncProviderMigrationState state) =>
        state is SyncProviderMigrationState.Completed or
            SyncProviderMigrationState.RolledBack or
            SyncProviderMigrationState.Failed;
}
