using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using SnapBoard.Application.Clipboard;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Application.Sync;

public sealed partial class SyncService
{
    private async ValueTask<SyncStatusSnapshot> ExecuteSingleFlightAsync(
        CancellationToken cancellationToken)
    {
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            if (Volatile.Read(ref _paused) != 0)
            {
                SyncStatusSnapshot paused = Status with
                {
                    State = SyncServiceState.Paused,
                    DiagnosticCode = null,
                };
                UpdateStatus(paused);
                return paused;
            }

            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            lock (_lifecycleGate)
            {
                _currentFlightCancellation = linkedCancellation;
            }

            CancellationToken token = linkedCancellation.Token;
            SyncConfigurationSnapshot? configuration = await _store.GetConfigurationAsync(token)
                .ConfigureAwait(false);
            if (configuration is null)
            {
                SyncStatusSnapshot notConfigured = new(SyncServiceState.NotConfigured);
                UpdateStatus(notConfigured);
                return notConfigured;
            }

            if (!configuration.IsEnabled)
            {
                SyncStatusSnapshot disabled = new(
                    SyncServiceState.Disabled,
                    configuration.SpaceId);
                UpdateStatus(disabled);
                return disabled;
            }

            UpdateStatus(Status with
            {
                State = SyncServiceState.Synchronizing,
                SpaceId = configuration.SpaceId,
                UploadedEvents = 0,
                DownloadedEvents = 0,
                DiagnosticCode = null,
            });
            SyncMasterKeyOpenResult keyResult = await _keyService.OpenMasterKeyAsync(
                    configuration.SpaceId,
                    configuration.KeyVersion,
                    token)
                .ConfigureAwait(false);
            if (keyResult.Status != SyncKeyOperationStatus.Success || keyResult.Key is null)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "master-key-unavailable",
                    keyUnavailable: true);
            }

            SyncCredentialOpenResult credentialResult = await _credentialService.OpenAsync(
                    configuration.SpaceId,
                    token)
                .ConfigureAwait(false);
            if (credentialResult.Status != SyncCredentialOperationStatus.Success ||
                credentialResult.Credential is null)
            {
                throw new SyncPipelineException(
                    credentialResult.Status == SyncCredentialOperationStatus.AccessDenied
                        ? SyncRemoteErrorCategory.Permission
                        : SyncRemoteErrorCategory.Authentication,
                    "webdav-credential-unavailable");
            }

            int uploaded;
            int downloaded;
            using (keyResult.Key)
            using (credentialResult.Credential)
            {
                await EnsureProviderMigrationAllowsUploadAsync(
                        configuration,
                        keyResult.Key.Key,
                        credentialResult.Credential,
                        token)
                    .ConfigureAwait(false);
                await using ISyncRemoteSession session = _remoteSessionFactory.Create(
                    credentialResult.Credential.RemoteConfiguration,
                    credentialResult.Credential.Password);
                await EnsureAndValidateMetadataAsync(
                            session,
                            configuration.SpaceId,
                            configuration.DeviceId,
                            configuration.KeyVersion,
                            keyResult.Key.Key,
                            createIfMissing: false,
                            token)
                        .ConfigureAwait(false);
                uploaded = await UploadAsync(
                            session,
                            configuration,
                            keyResult.Key.Key,
                            token)
                        .ConfigureAwait(false);
                downloaded = await DownloadAsync(
                            session,
                            configuration,
                            keyResult.Key.Key,
                            token)
                        .ConfigureAwait(false);
            }

            SyncStatusSnapshot success = new(
                SyncServiceState.Idle,
                configuration.SpaceId,
                DateTimeOffset.UtcNow,
                uploaded,
                downloaded);
            UpdateStatus(success);
            if (uploaded == _options.MaximumUploadBatch ||
                downloaded >= _options.MaximumDownloadBatchPerDevice)
            {
                RequestSync();
            }

            return success;
        }
        catch (OperationCanceledException) when (
            linkedCancellation?.IsCancellationRequested == true)
        {
            if (cancellationToken.IsCancellationRequested &&
                !_lifetimeCancellation.IsCancellationRequested &&
                Volatile.Read(ref _paused) == 0)
            {
                throw;
            }

            SyncStatusSnapshot cancelled = Status with
            {
                State = Volatile.Read(ref _paused) != 0
                    ? SyncServiceState.Paused
                    : Status.State,
                DiagnosticCode = null,
            };
            UpdateStatus(cancelled);
            return cancelled;
        }
        catch (SyncPipelineException exception)
        {
            SyncStatusSnapshot failure = Status with
            {
                State = exception.KeyUnavailable
                    ? SyncServiceState.KeyUnavailable
                    : MapServiceState(exception.Category),
                DiagnosticCode = exception.DiagnosticCode,
            };
            UpdateStatus(failure);
            return failure;
        }
        catch (CryptographicException)
        {
            SyncStatusSnapshot failure = Status with
            {
                State = SyncServiceState.Error,
                DiagnosticCode = "cryptographic-failure",
            };
            UpdateStatus(failure);
            return failure;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or JsonException or DbException or IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            SyncStatusSnapshot failure = Status with
            {
                State = SyncServiceState.Error,
                DiagnosticCode = exception is InvalidDataException or JsonException
                    ? "sync-protocol-invalid"
                    : "local-persistence-failure",
            };
            UpdateStatus(failure);
            return failure;
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_currentFlightCancellation, linkedCancellation))
                {
                    _currentFlightCancellation = null;
                }
            }

            linkedCancellation?.Dispose();
            _singleFlight.Release();
        }
    }

    private async ValueTask EnsureAndValidateMetadataAsync(
        ISyncRemoteSession session,
        Guid spaceId,
        Guid localDeviceId,
        int keyVersion,
        ReadOnlyMemory<byte> masterKey,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        SyncRemoteResult hierarchy = await session.EnsureHierarchyAsync(
                spaceId,
                localDeviceId,
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(hierarchy, "remote-hierarchy-failed");

        SyncRemoteContentResult metadataResult = await session.GetMetadataAsync(
                spaceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (metadataResult.Result.ErrorCategory == SyncRemoteErrorCategory.NotFound &&
            createIfMissing)
        {
            await CreateMetadataAsync(
                    session,
                    spaceId,
                    localDeviceId,
                    keyVersion,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ThrowIfRemoteFailure(metadataResult.Result, "remote-metadata-unavailable");
        if (metadataResult.Content is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "remote-metadata-empty");
        }

        using (metadataResult.Content)
        {
            ValidateMetadata(
                metadataResult.Content.Content.Span,
                spaceId,
                keyVersion,
                masterKey.Span);
        }
    }

    private async ValueTask CreateMetadataAsync(
        ISyncRemoteSession session,
        Guid spaceId,
        Guid localDeviceId,
        int keyVersion,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncSpaceMetadata metadata = new(
            SyncProtocol.CurrentVersion,
            spaceId,
            keyVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            metadata,
            SyncJsonContext.Default.SyncSpaceMetadata);
        byte[] encrypted = _protector.Encrypt(
            plaintext,
            new SyncObjectDescriptor(
                SyncProtocol.CurrentVersion,
                spaceId,
                localDeviceId,
                SyncObjectType.Metadata,
                0,
                "metadata",
                keyVersion),
            masterKey.Span);
        try
        {
            SyncRemoteResult put = await session.PutMetadataAsync(
                    spaceId,
                    encrypted,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRemoteFailure(put, "remote-metadata-write-failed");
            if (!put.AlreadyExisted)
            {
                return;
            }

            SyncRemoteContentResult existing = await session.GetMetadataAsync(
                    spaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRemoteFailure(existing.Result, "remote-metadata-race-failed");
            if (existing.Content is null)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "remote-metadata-empty");
            }

            using (existing.Content)
            {
                ValidateMetadata(
                    existing.Content.Content.Span,
                    spaceId,
                    keyVersion,
                    masterKey.Span);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void ValidateMetadata(
        ReadOnlySpan<byte> encrypted,
        Guid spaceId,
        int keyVersion,
        ReadOnlySpan<byte> masterKey)
    {
        SyncObjectDescriptor descriptor = _protector.ReadDescriptor(encrypted);
        if (descriptor.ProtocolVersion != SyncProtocol.CurrentVersion ||
            descriptor.SpaceId != spaceId ||
            descriptor.ObjectType != SyncObjectType.Metadata ||
            descriptor.Sequence != 0 || descriptor.ObjectId != "metadata" ||
            descriptor.KeyVersion != keyVersion)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "remote-metadata-descriptor-invalid");
        }

        byte[] plaintext = _protector.Decrypt(encrypted, descriptor, masterKey);
        byte[]? canonical = null;
        try
        {
            SyncSpaceMetadata metadata = JsonSerializer.Deserialize(
                    plaintext,
                    SyncJsonContext.Default.SyncSpaceMetadata) ??
                throw new InvalidDataException("Remote metadata is empty.");
            canonical = JsonSerializer.SerializeToUtf8Bytes(
                metadata,
                SyncJsonContext.Default.SyncSpaceMetadata);
            if (!canonical.AsSpan().SequenceEqual(plaintext) ||
                metadata.ProtocolVersion != SyncProtocol.CurrentVersion ||
                metadata.SpaceId != spaceId || metadata.KeyVersion != keyVersion)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "remote-metadata-payload-invalid");
            }

            try
            {
                _ = DateTimeOffset.FromUnixTimeMilliseconds(metadata.CreatedAtUnixMilliseconds);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new InvalidDataException("Remote metadata time is invalid.", exception);
            }
        }
        finally
        {
            if (canonical is not null)
            {
                CryptographicOperations.ZeroMemory(canonical);
            }

            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask<int> UploadAsync(
        ISyncRemoteSession session,
        SyncConfigurationSnapshot configuration,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SyncOutboxItem> outbox = await _store.ReadOutboxBatchAsync(
                _options.MaximumUploadBatch,
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        int uploaded = 0;
        foreach (SyncOutboxItem item in outbox)
        {
            try
            {
                await UploadReferencedBlobsAsync(
                        session,
                        configuration,
                        item.Event,
                        masterKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                SyncObjectDescriptor descriptor = new(
                    SyncProtocol.CurrentVersion,
                    configuration.SpaceId,
                    configuration.DeviceId,
                    SyncObjectType.Event,
                    item.Event.Sequence,
                    item.Event.EventId.ToString("N"),
                    configuration.KeyVersion);
                byte[] encryptedEvent = _protector.Encrypt(
                    item.SerializedEvent,
                    descriptor,
                    masterKey.Span);
                try
                {
                    SyncRemoteResult result = await session.PutEventAsync(
                            configuration.SpaceId,
                            configuration.DeviceId,
                            item.Event.Sequence,
                            item.Event.EventId,
                            encryptedEvent,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ThrowIfRemoteFailure(result, "event-upload-failed");
                    if (result.AlreadyExisted)
                    {
                        await VerifyExistingEventAsync(
                                session,
                                configuration,
                                item,
                                descriptor,
                                masterKey,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await _store.MarkOutboxUploadedAsync(
                            item.Event.EventId,
                            result.ETag,
                            DateTimeOffset.UtcNow,
                            cancellationToken)
                        .ConfigureAwait(false);
                    uploaded++;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encryptedEvent);
                }
            }
            catch (SyncPipelineException exception)
            {
                await _store.MarkOutboxFailedAsync(
                        item.Event.EventId,
                        MapPersistenceError(exception.Category),
                        GetNextAttempt(item.RetryCount, exception),
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
            catch (CryptographicException)
            {
                await _store.MarkOutboxFailedAsync(
                        item.Event.EventId,
                        SyncPersistenceErrorCategory.Cryptographic,
                        DateTimeOffset.UtcNow.AddMinutes(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
            catch (InvalidDataException)
            {
                await _store.MarkOutboxFailedAsync(
                        item.Event.EventId,
                        SyncPersistenceErrorCategory.Protocol,
                        DateTimeOffset.UtcNow.AddMinutes(15),
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
            finally
            {
                ZeroOutboxItem(item);
            }
        }

        return uploaded;
    }

    private async ValueTask UploadReferencedBlobsAsync(
        ISyncRemoteSession session,
        SyncConfigurationSnapshot configuration,
        SyncEventEnvelope syncEvent,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        foreach (SyncBlobReferencePayload reference in GetBlobReferences(syncEvent))
        {
            using SyncBlobLease lease = await _store.OpenBlobAsync(
                    reference.Hash,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException(
                    "An outbox event references a missing local Blob.");
            if (!string.Equals(lease.MediaType, reference.MediaType, StringComparison.Ordinal) ||
                lease.Content.Length != reference.SizeBytes)
            {
                throw new InvalidDataException("An outbox Blob metadata record is inconsistent.");
            }

            string keyedBlobId = _protector.ComputeKeyedBlobId(
                masterKey.Span,
                configuration.SpaceId,
                reference.Hash);
            SyncObjectDescriptor descriptor = CreateBlobDescriptor(
                configuration,
                keyedBlobId);
            byte[] encryptedBlob = _protector.Encrypt(
                lease.Content.Span,
                descriptor,
                masterKey.Span);
            try
            {
                SyncRemoteResult result = await session.PutBlobAsync(
                        configuration.SpaceId,
                        keyedBlobId,
                        encryptedBlob,
                        cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfRemoteFailure(result, "blob-upload-failed");
                if (result.AlreadyExisted)
                {
                    await VerifyExistingBlobAsync(
                            session,
                            configuration.SpaceId,
                            keyedBlobId,
                            descriptor,
                            reference,
                            masterKey,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedBlob);
            }
        }
    }

    private async ValueTask VerifyExistingEventAsync(
        ISyncRemoteSession session,
        SyncConfigurationSnapshot configuration,
        SyncOutboxItem item,
        SyncObjectDescriptor descriptor,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncRemoteContentResult existing = await session.GetEventAsync(
                configuration.SpaceId,
                new SyncRemoteEventReference(
                    configuration.DeviceId,
                    item.Event.Sequence,
                    item.Event.EventId,
                    null),
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(existing.Result, "event-idempotency-check-failed");
        if (existing.Content is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "event-idempotency-check-empty");
        }

        using (existing.Content)
        {
            byte[] plaintext = _protector.Decrypt(
                existing.Content.Content.Span,
                descriptor,
                masterKey.Span);
            try
            {
                if (!plaintext.AsSpan().SequenceEqual(item.SerializedEvent))
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "event-idempotency-conflict");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async ValueTask VerifyExistingBlobAsync(
        ISyncRemoteSession session,
        Guid spaceId,
        string keyedBlobId,
        SyncObjectDescriptor descriptor,
        SyncBlobReferencePayload reference,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncRemoteContentResult existing = await session.GetBlobAsync(
                spaceId,
                keyedBlobId,
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(existing.Result, "blob-idempotency-check-failed");
        if (existing.Content is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "blob-idempotency-check-empty");
        }

        using (existing.Content)
        {
            byte[] plaintext = _protector.Decrypt(
                existing.Content.Content.Span,
                descriptor,
                masterKey.Span);
            try
            {
                string hash = Convert.ToHexStringLower(SHA256.HashData(plaintext));
                if (plaintext.LongLength != reference.SizeBytes ||
                    !string.Equals(hash, reference.Hash, StringComparison.Ordinal))
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "blob-idempotency-conflict");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async ValueTask<int> DownloadAsync(
        ISyncRemoteSession session,
        SyncConfigurationSnapshot configuration,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncRemoteDeviceListResult deviceList = await session.ListDevicesAsync(
                configuration.SpaceId,
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(deviceList.Result, "device-list-failed");
        int downloaded = 0;
        foreach (Guid remoteDeviceId in deviceList.DeviceIds)
        {
            if (remoteDeviceId == configuration.DeviceId)
            {
                continue;
            }

            await _store.EnsureRemoteDeviceAsync(
                    configuration.SpaceId,
                    remoteDeviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncCheckpointState checkpoint = await _store.GetCheckpointAsync(
                    configuration.SpaceId,
                    remoteDeviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncRemoteEventListResult eventList = await session.ListEventsAsync(
                    configuration.SpaceId,
                    remoteDeviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRemoteFailure(eventList.Result, "event-list-failed");
            long remoteMaximum = eventList.Events.Count == 0
                ? 0
                : eventList.Events[^1].Sequence;
            if (checkpoint.AppliedSequence > remoteMaximum)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "checkpoint-ahead-of-remote");
            }

            long expectedSequence = checked(checkpoint.AppliedSequence + 1);
            int appliedForDevice = 0;
            foreach (SyncRemoteEventReference remoteEvent in eventList.Events.Where(
                candidate => candidate.Sequence >= expectedSequence))
            {
                if (appliedForDevice >= _options.MaximumDownloadBatchPerDevice)
                {
                    RequestSync();
                    break;
                }

                if (remoteEvent.Sequence != expectedSequence)
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "remote-event-sequence-gap");
                }

                await DownloadAndApplyEventAsync(
                        session,
                        configuration,
                        remoteEvent,
                        masterKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                expectedSequence++;
                appliedForDevice++;
                downloaded++;
            }
        }

        return downloaded;
    }

    private async ValueTask DownloadAndApplyEventAsync(
        ISyncRemoteSession session,
        SyncConfigurationSnapshot configuration,
        SyncRemoteEventReference remoteEvent,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncRemoteContentResult downloaded = await session.GetEventAsync(
                configuration.SpaceId,
                remoteEvent,
                cancellationToken)
            .ConfigureAwait(false);
        if (downloaded.Result.ErrorCategory == SyncRemoteErrorCategory.NotFound)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Transient,
                "enumerated-event-disappeared");
        }

        ThrowIfRemoteFailure(downloaded.Result, "event-download-failed");
        if (downloaded.Content is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "event-download-empty");
        }

        using (downloaded.Content)
        {
            SyncObjectDescriptor descriptor = new(
                SyncProtocol.CurrentVersion,
                configuration.SpaceId,
                remoteEvent.DeviceId,
                SyncObjectType.Event,
                remoteEvent.Sequence,
                remoteEvent.EventId.ToString("N"),
                configuration.KeyVersion);
            byte[] plaintext = _protector.Decrypt(
                downloaded.Content.Content.Span,
                descriptor,
                masterKey.Span);
            SyncEventEnvelope? syncEvent = null;
            try
            {
                syncEvent = JsonSerializer.Deserialize(
                        plaintext,
                        SyncJsonContext.Default.SyncEventEnvelope) ??
                    throw new InvalidDataException("A downloaded sync event is empty.");
                ValidateDownloadedEvent(syncEvent, remoteEvent, configuration.SpaceId, plaintext);
                await StageMissingBlobsAsync(
                        session,
                        configuration,
                        syncEvent,
                        masterKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                SyncEventApplyResult applied = await _store.ApplyRemoteEventAsync(
                        syncEvent,
                        plaintext,
                        downloaded.Result.ETag ?? remoteEvent.ETag,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (applied.Status == SyncEventApplyStatus.Applied &&
                    syncEvent.ChangeKind == SyncChangeKind.SetSetting)
                {
                    if (HistorySettingKeys.IsSynchronized(syncEvent.Setting!.Key) &&
                        _historySettingsService is not null)
                    {
                        await _historySettingsService.ApplyRemoteSettingAsync(
                                syncEvent.Setting.Key,
                                syncEvent.Setting.Value,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (string.Equals(
                        syncEvent.Setting!.Key,
                        SyncSettingKeys.PollInterval,
                        StringComparison.Ordinal))
                    {
                        await ApplyRemotePollingSettingAsync(
                                syncEvent.Setting.Value,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else if (applied.Status == SyncEventApplyStatus.Applied &&
                    _historyChangeNotifier is not null)
                {
                    ClipboardHistoryChangeKind changeKind = syncEvent.ChangeKind switch
                    {
                        SyncChangeKind.Delete => ClipboardHistoryChangeKind.Deleted,
                        SyncChangeKind.Upsert or SyncChangeKind.Restore =>
                            ClipboardHistoryChangeKind.Added,
                        _ => ClipboardHistoryChangeKind.Updated,
                    };
                    _historyChangeNotifier.Publish(new ClipboardHistoryChangedEvent(
                        changeKind,
                        new SnapBoard.Domain.Clipboard.ClipboardItemId(syncEvent.ItemId)));
                }

                if (applied.Status == SyncEventApplyStatus.SequenceGap ||
                    applied.ExpectedSequence != checked(remoteEvent.Sequence + 1))
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "local-checkpoint-sequence-gap");
                }
            }
            finally
            {
                ZeroSyncEvent(syncEvent);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async ValueTask StageMissingBlobsAsync(
        ISyncRemoteSession session,
        SyncConfigurationSnapshot configuration,
        SyncEventEnvelope syncEvent,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        foreach (SyncBlobReferencePayload reference in GetBlobReferences(syncEvent))
        {
            if (await _store.ContainsBlobAsync(
                    reference.Hash,
                    reference.MediaType,
                    reference.SizeBytes,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            string keyedBlobId = _protector.ComputeKeyedBlobId(
                masterKey.Span,
                configuration.SpaceId,
                reference.Hash);
            SyncRemoteContentResult downloaded = await session.GetBlobAsync(
                    configuration.SpaceId,
                    keyedBlobId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (downloaded.Result.ErrorCategory == SyncRemoteErrorCategory.NotFound)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "referenced-blob-missing");
            }

            ThrowIfRemoteFailure(downloaded.Result, "blob-download-failed");
            if (downloaded.Content is null)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "blob-download-empty");
            }

            using (downloaded.Content)
            {
                byte[] plaintext = _protector.Decrypt(
                    downloaded.Content.Content.Span,
                    CreateBlobDescriptor(configuration, keyedBlobId),
                    masterKey.Span);
                try
                {
                    await _store.StageDownloadedBlobAsync(
                            reference.Hash,
                            reference.MediaType,
                            plaintext,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
    }

    private static SyncBlobReferencePayload[] GetBlobReferences(
        SyncEventEnvelope syncEvent)
    {
        if (syncEvent.Item is null)
        {
            return [];
        }

        Dictionary<string, SyncBlobReferencePayload> references = new(StringComparer.Ordinal);
        foreach (SyncRepresentationPayload representation in syncEvent.Item.Representations)
        {
            if (representation.BlobHash is not null)
            {
                AddBlobReference(
                    references,
                    new SyncBlobReferencePayload(
                        representation.BlobHash,
                        representation.MediaType,
                        representation.SizeBytes));
            }
        }

        if (syncEvent.Item.Thumbnail is not null)
        {
            AddBlobReference(references, syncEvent.Item.Thumbnail);
        }

        if (syncEvent.Item.SourceApplicationIcon is not null)
        {
            AddBlobReference(references, syncEvent.Item.SourceApplicationIcon.Blob);
        }

        return references.Values.ToArray();
    }

    private static void AddBlobReference(
        Dictionary<string, SyncBlobReferencePayload> references,
        SyncBlobReferencePayload candidate)
    {
        if (references.TryGetValue(candidate.Hash, out SyncBlobReferencePayload? existing))
        {
            if (!string.Equals(existing.MediaType, candidate.MediaType, StringComparison.Ordinal) ||
                existing.SizeBytes != candidate.SizeBytes)
            {
                throw new InvalidDataException("A sync event contains conflicting Blob metadata.");
            }

            return;
        }

        references.Add(candidate.Hash, candidate);
    }

    private static void ValidateDownloadedEvent(
        SyncEventEnvelope syncEvent,
        SyncRemoteEventReference remoteEvent,
        Guid spaceId,
        ReadOnlySpan<byte> plaintext)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            syncEvent,
            SyncJsonContext.Default.SyncEventEnvelope);
        try
        {
            bool isSetting = syncEvent.ChangeKind == SyncChangeKind.SetSetting;
            if (!canonical.AsSpan().SequenceEqual(plaintext) ||
                syncEvent.ProtocolVersion != SyncProtocol.CurrentVersion ||
                syncEvent.SpaceId != spaceId ||
                syncEvent.DeviceId != remoteEvent.DeviceId ||
                syncEvent.Sequence != remoteEvent.Sequence ||
                syncEvent.EventId != remoteEvent.EventId ||
                (isSetting ? syncEvent.ItemId != Guid.Empty : syncEvent.ItemId == Guid.Empty) ||
                syncEvent.LogicalTimestamp <= 0)
            {
                throw new InvalidDataException("A downloaded event does not match its remote index.");
            }

            bool shapeValid = syncEvent.ChangeKind switch
            {
                SyncChangeKind.Upsert or SyncChangeKind.Restore =>
                    syncEvent.Item is not null && syncEvent.Tags is null &&
                    syncEvent.IsPinned is null && syncEvent.Setting is null,
                SyncChangeKind.SetTags =>
                    syncEvent.Item is null && syncEvent.Tags is not null &&
                    syncEvent.IsPinned is null && syncEvent.Setting is null,
                SyncChangeKind.SetPinned =>
                    syncEvent.Item is null && syncEvent.Tags is null &&
                    syncEvent.IsPinned is not null && syncEvent.Setting is null,
                SyncChangeKind.Delete =>
                    syncEvent.Item is null && syncEvent.Tags is null &&
                    syncEvent.IsPinned is null && syncEvent.Setting is null,
                SyncChangeKind.SetSetting =>
                    syncEvent.Item is null && syncEvent.Tags is null &&
                    syncEvent.IsPinned is null && syncEvent.Setting is not null &&
                    SynchronizedSettingRegistry.IsValidValue(
                        syncEvent.Setting.Key,
                        syncEvent.Setting.Value),
                _ => false,
            };
            if (!shapeValid)
            {
                throw new InvalidDataException("A downloaded event has an invalid shape.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static SyncObjectDescriptor CreateBlobDescriptor(
        SyncConfigurationSnapshot configuration,
        string keyedBlobId) => new(
        SyncProtocol.CurrentVersion,
        configuration.SpaceId,
        // 共享 Blob 没有单一上传设备，使用空间 ID 作为协议规定的稳定命名主体。
        configuration.SpaceId,
        SyncObjectType.Blob,
        0,
        keyedBlobId,
        configuration.KeyVersion);

    private static void ThrowIfRemoteFailure(
        SyncRemoteResult result,
        string diagnosticCode)
    {
        if (!result.IsSuccess)
        {
            throw new SyncPipelineException(
                result.ErrorCategory,
                diagnosticCode,
                result.RetryAfter);
        }
    }

    private static SyncServiceState MapServiceState(SyncRemoteErrorCategory category) =>
        category switch
        {
            SyncRemoteErrorCategory.Authentication => SyncServiceState.AuthenticationRequired,
            SyncRemoteErrorCategory.Permission => SyncServiceState.PermissionDenied,
            _ => SyncServiceState.Error,
        };

    private static SyncPersistenceErrorCategory MapPersistenceError(
        SyncRemoteErrorCategory category) => category switch
        {
            SyncRemoteErrorCategory.Authentication => SyncPersistenceErrorCategory.Authentication,
            SyncRemoteErrorCategory.Permission => SyncPersistenceErrorCategory.Permission,
            SyncRemoteErrorCategory.AlreadyExists => SyncPersistenceErrorCategory.Conflict,
            SyncRemoteErrorCategory.RateLimited => SyncPersistenceErrorCategory.RateLimited,
            SyncRemoteErrorCategory.Transient or SyncRemoteErrorCategory.Timeout =>
                SyncPersistenceErrorCategory.Transient,
            SyncRemoteErrorCategory.Network => SyncPersistenceErrorCategory.Network,
            _ => SyncPersistenceErrorCategory.Protocol,
        };

    private static DateTimeOffset GetNextAttempt(
        int retryCount,
        SyncPipelineException exception)
    {
        TimeSpan delay;
        if (exception.Category is SyncRemoteErrorCategory.Authentication or
            SyncRemoteErrorCategory.Permission or SyncRemoteErrorCategory.Certificate or
            SyncRemoteErrorCategory.Protocol or SyncRemoteErrorCategory.ResponseTooLarge)
        {
            delay = TimeSpan.FromMinutes(15);
        }
        else if (exception.RetryAfter is TimeSpan retryAfter)
        {
            delay = retryAfter > TimeSpan.FromMinutes(15)
                ? TimeSpan.FromMinutes(15)
                : retryAfter;
        }
        else
        {
            int seconds = 1 << Math.Min(retryCount, 8);
            delay = TimeSpan.FromSeconds(seconds);
        }

        return DateTimeOffset.UtcNow.Add(delay);
    }

    private static void ZeroOutboxItem(SyncOutboxItem item)
    {
        CryptographicOperations.ZeroMemory(item.SerializedEvent);
        ZeroSyncEvent(item.Event);
    }

    private static void ZeroSyncEvent(SyncEventEnvelope? syncEvent)
    {
        if (syncEvent?.Item is null)
        {
            return;
        }

        foreach (SyncRepresentationPayload representation in syncEvent.Item.Representations)
        {
            if (representation.InlineData is not null)
            {
                CryptographicOperations.ZeroMemory(representation.InlineData);
            }
        }
    }

    private sealed class SyncPipelineException : Exception
    {
        public SyncPipelineException(
            SyncRemoteErrorCategory category,
            string diagnosticCode,
            TimeSpan? retryAfter = null,
            bool keyUnavailable = false)
            : base(diagnosticCode)
        {
            Category = category;
            DiagnosticCode = diagnosticCode;
            RetryAfter = retryAfter;
            KeyUnavailable = keyUnavailable;
        }

        public SyncRemoteErrorCategory Category { get; }

        public string DiagnosticCode { get; }

        public TimeSpan? RetryAfter { get; }

        public bool KeyUnavailable { get; }
    }
}
