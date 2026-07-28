using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SnapBoard.Sync.Contracts;
using SnapBoard.Sync.Contracts.Serialization;

namespace SnapBoard.Application.Sync;

public sealed partial class SyncService
{
    private const int MaximumProviderMigrationPlans = 1024;
    private const int MaximumProviderMigrationObjects = 1_000_000;

    private async ValueTask<RemoteProviderMigrationScan> ScanProviderMigrationsAsync(
        ISyncRemoteProviderMigrationSession session,
        SyncConfigurationSnapshot configuration,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncRemoteProviderMigrationPlanListResult list = await session
            .ListProviderMigrationPlansAsync(configuration.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(list.Result, "provider-migration-plan-list-failed");
        if (list.Plans.Count > MaximumProviderMigrationPlans)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-plan-count-invalid");
        }

        SyncProviderMigrationIntent? latest = null;
        bool latestRolledBack = false;
        bool latestCompleted = false;
        HashSet<long> epochs = [];
        foreach (SyncRemoteProviderMigrationPlanReference plan in list.Plans)
        {
            SyncProviderMigrationIntent? intent = await ReadProviderMigrationIntentAsync(
                    session,
                    configuration,
                    plan.PlanId,
                    masterKey,
                    allowNotFound: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (intent is null)
            {
                continue;
            }

            if (!epochs.Add(intent.Epoch))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-epoch-reused");
            }

            SyncProviderMigrationDecision? rollback = await ReadDecisionMarkerAsync(
                    session,
                    configuration,
                    intent,
                    SyncProviderMigrationMarkerKind.Rollback,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncProviderMigrationDecision? completed = await ReadDecisionMarkerAsync(
                    session,
                    configuration,
                    intent,
                    SyncProviderMigrationMarkerKind.Completed,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (rollback is not null && completed is not null)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-terminal-markers-conflict");
            }

            if (latest is null || intent.Epoch > latest.Epoch)
            {
                latest = intent;
                latestRolledBack = rollback is not null;
                latestCompleted = completed is not null;
            }
        }

        return new RemoteProviderMigrationScan(
            latest,
            latest?.Epoch ?? 0,
            latestRolledBack,
            latestCompleted);
    }

    private async ValueTask<SyncProviderMigrationIntent> ReadRequiredIntentAsync(
        ISyncRemoteProviderMigrationSession session,
        SyncConfigurationSnapshot configuration,
        Guid planId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken) =>
        await ReadProviderMigrationIntentAsync(
                session,
                configuration,
                planId,
                masterKey,
                allowNotFound: false,
                cancellationToken)
            .ConfigureAwait(false) ?? throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-intent-missing");

    private async ValueTask<SyncProviderMigrationIntent?> ReadProviderMigrationIntentAsync(
        ISyncRemoteProviderMigrationSession session,
        SyncConfigurationSnapshot configuration,
        Guid planId,
        ReadOnlyMemory<byte> masterKey,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        SyncRemoteContentResult result = await session.GetProviderMigrationMarkerAsync(
                configuration.SpaceId,
                new SyncProviderMigrationMarkerAddress(
                    planId,
                    SyncProviderMigrationMarkerKind.Intent),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Result.ErrorCategory == SyncRemoteErrorCategory.NotFound && allowNotFound)
        {
            return null;
        }

        ThrowIfRemoteFailure(result.Result, "provider-migration-intent-read-failed");
        if (result.Content is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-intent-empty");
        }

        using (result.Content)
        {
            SyncObjectDescriptor descriptor = _protector.ReadDescriptor(
                result.Content.Content.Span);
            if (descriptor.ProtocolVersion != SyncProtocol.CurrentVersion ||
                descriptor.SpaceId != configuration.SpaceId ||
                descriptor.ObjectType != SyncObjectType.ProviderMigration ||
                descriptor.Sequence <= 0 ||
                !string.Equals(
                    descriptor.ObjectId,
                    planId.ToString("N"),
                    StringComparison.Ordinal) ||
                descriptor.KeyVersion != configuration.KeyVersion)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-intent-descriptor-invalid");
            }

            byte[] plaintext = _protector.Decrypt(
                result.Content.Content.Span,
                descriptor,
                masterKey.Span);
            try
            {
                SyncProviderMigrationIntent intent = DeserializeCanonical(
                    plaintext,
                    SyncJsonContext.Default.SyncProviderMigrationIntent,
                    "provider-migration-intent-payload-invalid");
                ValidateProviderMigrationIntent(intent, configuration.SpaceId, planId);
                if (descriptor.DeviceId != intent.InitiatorDeviceId ||
                    descriptor.Sequence != intent.Epoch)
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "provider-migration-intent-descriptor-mismatch");
                }

                return intent;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async ValueTask<SyncProviderMigrationDecision?> ReadDecisionMarkerAsync(
        ISyncRemoteProviderMigrationSession session,
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationIntent intent,
        SyncProviderMigrationMarkerKind kind,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncProviderMigrationMarkerAddress address = new(intent.PlanId, kind);
        SyncRemoteContentResult result = await session.GetProviderMigrationMarkerAsync(
                configuration.SpaceId,
                address,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Result.ErrorCategory == SyncRemoteErrorCategory.NotFound)
        {
            return null;
        }

        ThrowIfRemoteFailure(result.Result, "provider-migration-decision-read-failed");
        if (result.Content is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-decision-empty");
        }

        using (result.Content)
        {
            SyncObjectDescriptor descriptor = CreateMigrationDescriptor(
                configuration,
                intent.PlanId,
                intent.Epoch,
                intent.InitiatorDeviceId);
            byte[] plaintext = _protector.Decrypt(
                result.Content.Content.Span,
                descriptor,
                masterKey.Span);
            try
            {
                SyncProviderMigrationDecision decision = DeserializeCanonical(
                    plaintext,
                    SyncJsonContext.Default.SyncProviderMigrationDecision,
                    "provider-migration-decision-payload-invalid");
                ValidateDecision(decision, intent, kind);
                return decision;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async ValueTask<SyncProviderMigrationDeviceMarker?> ReadDeviceMarkerAsync(
        ISyncRemoteProviderMigrationSession session,
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationIntent intent,
        SyncProviderMigrationMarkerKind kind,
        Guid deviceId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncProviderMigrationMarkerAddress address = new(intent.PlanId, kind, deviceId);
        SyncRemoteContentResult result = await session.GetProviderMigrationMarkerAsync(
                configuration.SpaceId,
                address,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Result.ErrorCategory == SyncRemoteErrorCategory.NotFound)
        {
            return null;
        }

        ThrowIfRemoteFailure(result.Result, "provider-migration-device-marker-read-failed");
        if (result.Content is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-device-marker-empty");
        }

        using (result.Content)
        {
            SyncObjectDescriptor descriptor = CreateMigrationDescriptor(
                configuration,
                intent.PlanId,
                intent.Epoch,
                deviceId);
            byte[] plaintext = _protector.Decrypt(
                result.Content.Content.Span,
                descriptor,
                masterKey.Span);
            try
            {
                SyncProviderMigrationDeviceMarker marker = DeserializeCanonical(
                    plaintext,
                    SyncJsonContext.Default.SyncProviderMigrationDeviceMarker,
                    "provider-migration-device-marker-payload-invalid");
                ValidateDeviceMarker(marker, intent, kind, deviceId);
                return marker;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private async ValueTask<IReadOnlyDictionary<Guid, SyncProviderMigrationDeviceMarker>>
        ReadDeviceMarkersAsync(
            ISyncRemoteProviderMigrationSession session,
            SyncConfigurationSnapshot configuration,
            SyncProviderMigrationIntent intent,
            SyncProviderMigrationMarkerKind kind,
            ReadOnlyMemory<byte> masterKey,
            CancellationToken cancellationToken)
    {
        Dictionary<Guid, SyncProviderMigrationDeviceMarker> markers = [];
        foreach (Guid deviceId in intent.RequiredDeviceIds)
        {
            SyncProviderMigrationDeviceMarker? marker = await ReadDeviceMarkerAsync(
                    session,
                    configuration,
                    intent,
                    kind,
                    deviceId,
                    masterKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (marker is not null)
            {
                markers.Add(deviceId, marker);
            }
        }

        return markers;
    }

    private async ValueTask PutIntentMarkerAsync(
        ISyncRemoteProviderMigrationSession session,
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationIntent intent,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncProviderMigrationIntent? existing = await ReadProviderMigrationIntentAsync(
                session,
                configuration,
                intent.PlanId,
                masterKey,
                allowNotFound: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!ProviderMigrationIntentsEqual(existing, intent))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-intent-conflict");
            }

            return;
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            intent,
            SyncJsonContext.Default.SyncProviderMigrationIntent);
        byte[] encrypted = _protector.Encrypt(
            plaintext,
            CreateMigrationDescriptor(
                configuration,
                intent.PlanId,
                intent.Epoch,
                intent.InitiatorDeviceId),
            masterKey.Span);
        try
        {
            SyncRemoteResult put = await session.PutProviderMigrationMarkerAsync(
                    configuration.SpaceId,
                    new SyncProviderMigrationMarkerAddress(
                        intent.PlanId,
                        SyncProviderMigrationMarkerKind.Intent),
                    encrypted,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRemoteFailure(put, "provider-migration-intent-write-failed");
            if (put.AlreadyExisted)
            {
                existing = await ReadProviderMigrationIntentAsync(
                        session,
                        configuration,
                        intent.PlanId,
                        masterKey,
                        allowNotFound: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null || !ProviderMigrationIntentsEqual(existing, intent))
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "provider-migration-intent-conflict");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask PutDeviceMarkerAsync(
        ISyncRemoteProviderMigrationSession session,
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationIntent intent,
        SyncProviderMigrationDeviceMarker marker,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncProviderMigrationDeviceMarker? existing = await ReadDeviceMarkerAsync(
                session,
                configuration,
                intent,
                marker.Kind,
                marker.DeviceId,
                masterKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!DeviceMarkersEqual(existing, marker))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-device-marker-conflict");
            }

            return;
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            marker,
            SyncJsonContext.Default.SyncProviderMigrationDeviceMarker);
        byte[] encrypted = _protector.Encrypt(
            plaintext,
            CreateMigrationDescriptor(
                configuration,
                intent.PlanId,
                intent.Epoch,
                marker.DeviceId),
            masterKey.Span);
        try
        {
            SyncRemoteResult put = await session.PutProviderMigrationMarkerAsync(
                    configuration.SpaceId,
                    new SyncProviderMigrationMarkerAddress(
                        intent.PlanId,
                        marker.Kind,
                        marker.DeviceId),
                    encrypted,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRemoteFailure(put, "provider-migration-device-marker-write-failed");
            if (put.AlreadyExisted)
            {
                existing = await ReadDeviceMarkerAsync(
                        session,
                        configuration,
                        intent,
                        marker.Kind,
                        marker.DeviceId,
                        masterKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null || !DeviceMarkersEqual(existing, marker))
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "provider-migration-device-marker-conflict");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask PutDecisionMarkerAsync(
        ISyncRemoteProviderMigrationSession session,
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationIntent intent,
        SyncProviderMigrationDecision decision,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        SyncProviderMigrationDecision? existing = await ReadDecisionMarkerAsync(
                session,
                configuration,
                intent,
                decision.Kind,
                masterKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!DecisionsEqual(existing, decision))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-decision-conflict");
            }

            return;
        }

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            decision,
            SyncJsonContext.Default.SyncProviderMigrationDecision);
        byte[] encrypted = _protector.Encrypt(
            plaintext,
            CreateMigrationDescriptor(
                configuration,
                intent.PlanId,
                intent.Epoch,
                intent.InitiatorDeviceId),
            masterKey.Span);
        try
        {
            SyncRemoteResult put = await session.PutProviderMigrationMarkerAsync(
                    configuration.SpaceId,
                    new SyncProviderMigrationMarkerAddress(intent.PlanId, decision.Kind),
                    encrypted,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRemoteFailure(put, "provider-migration-decision-write-failed");
            if (put.AlreadyExisted)
            {
                existing = await ReadDecisionMarkerAsync(
                        session,
                        configuration,
                        intent,
                        decision.Kind,
                        masterKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null || !DecisionsEqual(existing, decision))
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "provider-migration-decision-conflict");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask<SyncProviderMigrationRecord>
        MirrorAndVerifyProviderMigrationAsync(
            ISyncRemoteProviderMigrationSession source,
            ISyncRemoteProviderMigrationSession target,
            SyncConfigurationSnapshot configuration,
            SyncProviderMigrationRecord migration,
            SyncProviderMigrationIntent intent,
            IReadOnlyDictionary<Guid, SyncProviderMigrationDeviceMarker> readyMarkers,
            ReadOnlyMemory<byte> masterKey,
            CancellationToken cancellationToken)
    {
        SyncRemoteCiphertextObjectListResult sourceList = await source
            .ListCiphertextObjectsAsync(configuration.SpaceId, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(sourceList.Result, "provider-migration-source-list-failed");
        if (sourceList.Objects.Count is < 1 or > MaximumProviderMigrationObjects ||
            sourceList.Objects.Count(static item => item.ObjectType == SyncObjectType.Metadata) != 1)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-source-inventory-invalid");
        }

        string[] identities = sourceList.Objects.Select(GetCiphertextIdentity).ToArray();
        if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length ||
            !identities.SequenceEqual(identities.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-source-order-invalid");
        }

        migration = migration with
        {
            State = SyncProviderMigrationState.MirroringCiphertext,
            TotalObjects = sourceList.Objects.Count,
            TotalBytes = 0,
            CompletedObjects = 0,
            CompletedBytes = 0,
            InventorySha256 = null,
            DiagnosticCode = null,
            UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await _store.SaveProviderMigrationAsync(migration, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, SyncBlobReferencePayload> expectedBlobs = new(StringComparer.Ordinal);
        Dictionary<Guid, long> remoteMaximums = intent.RequiredDeviceIds.ToDictionary(
            static deviceId => deviceId,
            static _ => 0L);
        Dictionary<Guid, long> nextSequences = intent.RequiredDeviceIds.ToDictionary(
            static deviceId => deviceId,
            static _ => 1L);
        Dictionary<(Guid DeviceId, long Sequence), Guid> remoteEventIds = [];
        List<CiphertextInventoryEntry> inventory = [];
        long completedBytes = 0;
        foreach ((SyncRemoteCiphertextObjectReference reference, int index) in
            sourceList.Objects.Select(static (item, index) => (item, index)))
        {
            SyncRemoteContentResult contentResult = await source.GetCiphertextObjectAsync(
                    configuration.SpaceId,
                    reference,
                    cancellationToken)
                .ConfigureAwait(false);
            if (contentResult.Result.ErrorCategory == SyncRemoteErrorCategory.NotFound)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Transient,
                    "provider-migration-source-object-disappeared");
            }

            ThrowIfRemoteFailure(
                contentResult.Result,
                "provider-migration-source-object-read-failed");
            if (contentResult.Content is null || contentResult.Content.Content.IsEmpty)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-source-object-empty");
            }

            using (contentResult.Content)
            {
                ReadOnlyMemory<byte> encrypted = contentResult.Content.Content;
                if (reference.ContentLength is long expectedLength &&
                    expectedLength != encrypted.Length)
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "provider-migration-source-size-mismatch");
                }

                ValidateMigratedCiphertext(
                    configuration,
                    reference,
                    encrypted.Span,
                    masterKey.Span,
                    expectedBlobs,
                    nextSequences,
                    remoteMaximums,
                    remoteEventIds);
                byte[] contentHash = SHA256.HashData(encrypted.Span);
                string hash = Convert.ToHexStringLower(contentHash);
                CryptographicOperations.ZeroMemory(contentHash);
                SyncRemoteResult put = await target.PutCiphertextObjectAsync(
                        configuration.SpaceId,
                        reference,
                        encrypted,
                        cancellationToken)
                    .ConfigureAwait(false);
                ThrowIfRemoteFailure(put, "provider-migration-target-object-write-failed");
                if (put.AlreadyExisted)
                {
                    await VerifyTargetCiphertextAsync(
                            target,
                            configuration.SpaceId,
                            reference,
                            encrypted,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                completedBytes = checked(completedBytes + encrypted.Length);
                inventory.Add(new CiphertextInventoryEntry(
                    GetCiphertextIdentity(reference),
                    encrypted.Length,
                    hash));
            }

            migration = migration with
            {
                TotalBytes = completedBytes,
                CompletedObjects = index + 1,
                CompletedBytes = completedBytes,
                UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await _store.SaveProviderMigrationAsync(migration, cancellationToken)
                .ConfigureAwait(false);
            PublishProviderMigration(new SyncProviderMigrationSnapshot(
                migration.State,
                migration.PlanId,
                migration.SpaceId,
                migration.Epoch,
                migration.InitiatorDeviceId,
                intent.SourceEndpoint,
                intent.SourceRemoteRoot,
                intent.TargetEndpoint,
                intent.TargetRemoteRoot,
                TotalObjects: migration.TotalObjects,
                TotalBytes: migration.TotalBytes,
                CompletedObjects: migration.CompletedObjects,
                CompletedBytes: migration.CompletedBytes));
        }

        HashSet<string> blobObjectIds = sourceList.Objects
            .Where(static item => item.ObjectType == SyncObjectType.Blob)
            .Select(static item => item.KeyedBlobId!)
            .ToHashSet(StringComparer.Ordinal);
        if (expectedBlobs.Keys.Any(keyedBlobId => !blobObjectIds.Contains(keyedBlobId)))
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-referenced-blob-missing");
        }

        ValidateReadyWatermarks(
            intent,
            readyMarkers,
            remoteMaximums,
            remoteEventIds);
        string inventorySha256 = ComputeInventoryHash(inventory);
        migration = migration with
        {
            State = SyncProviderMigrationState.VerifyingTarget,
            TotalBytes = completedBytes,
            CompletedObjects = inventory.Count,
            CompletedBytes = completedBytes,
            InventorySha256 = inventorySha256,
            UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await _store.SaveProviderMigrationAsync(migration, cancellationToken)
            .ConfigureAwait(false);
        await VerifyTargetInventoryAsync(
                target,
                configuration.SpaceId,
                inventory,
                cancellationToken)
            .ConfigureAwait(false);
        return migration;
    }

    private void ValidateMigratedCiphertext(
        SyncConfigurationSnapshot configuration,
        SyncRemoteCiphertextObjectReference reference,
        ReadOnlySpan<byte> encrypted,
        ReadOnlySpan<byte> masterKey,
        Dictionary<string, SyncBlobReferencePayload> expectedBlobs,
        Dictionary<Guid, long> nextSequences,
        Dictionary<Guid, long> remoteMaximums,
        Dictionary<(Guid DeviceId, long Sequence), Guid> remoteEventIds)
    {
        switch (reference.ObjectType)
        {
            case SyncObjectType.Metadata:
                ValidateMetadata(
                    encrypted,
                    configuration.SpaceId,
                    configuration.KeyVersion,
                    masterKey);
                return;

            case SyncObjectType.Event when reference.DeviceId is Guid deviceId &&
                reference.EventId is Guid eventId:
                {
                    if (!nextSequences.TryGetValue(deviceId, out long expectedSequence) ||
                        reference.Sequence != expectedSequence)
                    {
                        throw new SyncPipelineException(
                            SyncRemoteErrorCategory.Protocol,
                            "provider-migration-event-sequence-gap");
                    }

                    SyncObjectDescriptor descriptor = new(
                        SyncProtocol.CurrentVersion,
                        configuration.SpaceId,
                        deviceId,
                        SyncObjectType.Event,
                        reference.Sequence,
                        eventId.ToString("N"),
                        configuration.KeyVersion);
                    byte[] plaintext = _protector.Decrypt(encrypted, descriptor, masterKey);
                    SyncEventEnvelope? syncEvent = null;
                    try
                    {
                        syncEvent = JsonSerializer.Deserialize(
                                plaintext,
                                SyncJsonContext.Default.SyncEventEnvelope) ??
                            throw new InvalidDataException("A migrated sync event is empty.");
                        ValidateDownloadedEvent(
                            syncEvent,
                            new SyncRemoteEventReference(
                                deviceId,
                                reference.Sequence,
                                eventId,
                                reference.ETag),
                            configuration.SpaceId,
                            plaintext);
                        foreach (SyncBlobReferencePayload blob in GetBlobReferences(syncEvent))
                        {
                            string keyedBlobId = _protector.ComputeKeyedBlobId(
                                masterKey,
                                configuration.SpaceId,
                                blob.Hash);
                            if (expectedBlobs.TryGetValue(
                                    keyedBlobId,
                                    out SyncBlobReferencePayload? existing) &&
                                (!string.Equals(existing.Hash, blob.Hash, StringComparison.Ordinal) ||
                                    !string.Equals(
                                        existing.MediaType,
                                        blob.MediaType,
                                        StringComparison.Ordinal) ||
                                    existing.SizeBytes != blob.SizeBytes))
                            {
                                throw new SyncPipelineException(
                                    SyncRemoteErrorCategory.Protocol,
                                    "provider-migration-blob-reference-conflict");
                            }

                            expectedBlobs[keyedBlobId] = blob;
                        }
                    }
                    finally
                    {
                        ZeroSyncEvent(syncEvent);
                        CryptographicOperations.ZeroMemory(plaintext);
                    }

                    nextSequences[deviceId] = checked(reference.Sequence + 1);
                    remoteMaximums[deviceId] = reference.Sequence;
                    remoteEventIds.Add((deviceId, reference.Sequence), eventId);
                    return;
                }

            case SyncObjectType.Blob when reference.KeyedBlobId is string keyedBlobId:
                {
                    byte[] plaintext = _protector.Decrypt(
                        encrypted,
                        CreateBlobDescriptor(configuration, keyedBlobId),
                        masterKey);
                    byte[] plaintextHash = SHA256.HashData(plaintext);
                    try
                    {
                        string hash = Convert.ToHexStringLower(plaintextHash);
                        string computedKeyedBlobId = _protector.ComputeKeyedBlobId(
                            masterKey,
                            configuration.SpaceId,
                            hash);
                        if (!string.Equals(
                                computedKeyedBlobId,
                                keyedBlobId,
                                StringComparison.Ordinal))
                        {
                            throw new SyncPipelineException(
                                SyncRemoteErrorCategory.Protocol,
                                "provider-migration-blob-identity-invalid");
                        }

                        if (expectedBlobs.TryGetValue(
                                keyedBlobId,
                                out SyncBlobReferencePayload? expected))
                        {
                            if (plaintext.LongLength != expected.SizeBytes ||
                                !string.Equals(hash, expected.Hash, StringComparison.Ordinal))
                            {
                                throw new SyncPipelineException(
                                    SyncRemoteErrorCategory.Protocol,
                                    "provider-migration-blob-content-invalid");
                            }
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintextHash);
                        CryptographicOperations.ZeroMemory(plaintext);
                    }

                    return;
                }

            default:
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-object-reference-invalid");
        }
    }

    private static void ValidateReadyWatermarks(
        SyncProviderMigrationIntent intent,
        IReadOnlyDictionary<Guid, SyncProviderMigrationDeviceMarker> readyMarkers,
        Dictionary<Guid, long> remoteMaximums,
        Dictionary<(Guid DeviceId, long Sequence), Guid> remoteEventIds)
    {
        foreach (Guid deviceId in intent.RequiredDeviceIds)
        {
            if (!readyMarkers.TryGetValue(
                    deviceId,
                    out SyncProviderMigrationDeviceMarker? ready) ||
                ready.HighestUploadedSequence != remoteMaximums[deviceId] ||
                ready.HighestLocalSequence < ready.HighestUploadedSequence)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-ready-watermark-invalid");
            }

            Dictionary<Guid, SyncProviderMigrationCheckpoint> checkpoints = ready.Checkpoints
                .ToDictionary(static checkpoint => checkpoint.DeviceId);
            if (checkpoints.Keys.Any(remoteDeviceId =>
                    !intent.RequiredDeviceIds.Contains(remoteDeviceId)))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-ready-checkpoint-device-invalid");
            }

            foreach (Guid remoteDeviceId in intent.RequiredDeviceIds.Where(
                remoteDeviceId => remoteDeviceId != deviceId))
            {
                if (!checkpoints.TryGetValue(
                        remoteDeviceId,
                        out SyncProviderMigrationCheckpoint? checkpoint))
                {
                    if (remoteMaximums[remoteDeviceId] == 0)
                    {
                        continue;
                    }

                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "provider-migration-ready-checkpoint-invalid");
                }

                if (checkpoint.AppliedSequence != remoteMaximums[remoteDeviceId])
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "provider-migration-ready-checkpoint-invalid");
                }

                Guid? expectedEventId = checkpoint.AppliedSequence == 0
                    ? null
                    : remoteEventIds[(remoteDeviceId, checkpoint.AppliedSequence)];
                if (checkpoint.AppliedEventId != expectedEventId)
                {
                    throw new SyncPipelineException(
                        SyncRemoteErrorCategory.Protocol,
                        "provider-migration-ready-checkpoint-event-invalid");
                }
            }
        }
    }

    private static async ValueTask VerifyTargetCiphertextAsync(
        ISyncRemoteProviderMigrationSession target,
        Guid spaceId,
        SyncRemoteCiphertextObjectReference reference,
        ReadOnlyMemory<byte> expected,
        CancellationToken cancellationToken)
    {
        SyncRemoteContentResult result = await target.GetCiphertextObjectAsync(
                spaceId,
                reference,
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(result.Result, "provider-migration-target-conflict-read-failed");
        if (result.Content is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-target-conflict-empty");
        }

        using (result.Content)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    result.Content.Content.Span,
                    expected.Span))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.AlreadyExists,
                    "provider-migration-target-object-conflict");
            }
        }
    }

    private static async ValueTask VerifyTargetInventoryAsync(
        ISyncRemoteProviderMigrationSession target,
        Guid spaceId,
        IReadOnlyList<CiphertextInventoryEntry> sourceInventory,
        CancellationToken cancellationToken)
    {
        SyncRemoteCiphertextObjectListResult targetList = await target
            .ListCiphertextObjectsAsync(spaceId, cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(targetList.Result, "provider-migration-target-list-failed");
        string[] targetIdentities = targetList.Objects.Select(GetCiphertextIdentity).ToArray();
        string[] sourceIdentities = sourceInventory.Select(static entry => entry.Identity).ToArray();
        if (!sourceIdentities.SequenceEqual(targetIdentities, StringComparer.Ordinal))
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-target-inventory-mismatch");
        }

        for (int index = 0; index < targetList.Objects.Count; index++)
        {
            SyncRemoteContentResult result = await target.GetCiphertextObjectAsync(
                    spaceId,
                    targetList.Objects[index],
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfRemoteFailure(result.Result, "provider-migration-target-verify-read-failed");
            if (result.Content is null)
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-target-verify-empty");
            }

            using (result.Content)
            {
                CiphertextInventoryEntry expected = sourceInventory[index];
                byte[] hash = SHA256.HashData(result.Content.Content.Span);
                try
                {
                    byte[] expectedHash = Convert.FromHexString(expected.Sha256);
                    try
                    {
                        if (result.Content.Content.Length != expected.Length ||
                            !CryptographicOperations.FixedTimeEquals(hash, expectedHash))
                        {
                            throw new SyncPipelineException(
                                SyncRemoteErrorCategory.Protocol,
                                "provider-migration-target-content-mismatch");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(expectedHash);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
            }
        }
    }

    private static string ComputeInventoryHash(
        IReadOnlyList<CiphertextInventoryEntry> inventory)
    {
        StringBuilder builder = new();
        foreach (CiphertextInventoryEntry entry in inventory)
        {
            builder.Append(entry.Identity)
                .Append('\t')
                .Append(entry.Length.ToString(CultureInfo.InvariantCulture))
                .Append('\t')
                .Append(entry.Sha256)
                .Append('\n');
        }

        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string GetCiphertextIdentity(
        SyncRemoteCiphertextObjectReference reference) => reference.ObjectType switch
        {
            SyncObjectType.Metadata when reference.DeviceId is null &&
                reference.Sequence == 0 && reference.EventId is null &&
                reference.KeyedBlobId is null => "1/metadata",
            SyncObjectType.Event when reference.DeviceId is Guid deviceId &&
                reference.Sequence > 0 && reference.EventId is Guid eventId &&
                reference.KeyedBlobId is null => string.Create(
                    CultureInfo.InvariantCulture,
                    $"2/{deviceId:N}/{reference.Sequence:D20}/{eventId:N}"),
            SyncObjectType.Blob when reference.DeviceId is null &&
                reference.Sequence == 0 && reference.EventId is null &&
                reference.KeyedBlobId is string keyedBlobId &&
                SyncRemoteLayout.IsLowerHex(keyedBlobId, 64) => $"3/{keyedBlobId}",
            _ => throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-object-reference-invalid"),
        };

    private static SyncProviderMigrationIntent CreateProviderMigrationIntent(
        SyncProviderMigrationRecord migration,
        SyncRemoteConfiguration source,
        SyncRemoteConfiguration target,
        Guid[] requiredDeviceIds) => new(
        SyncProviderMigrationProtocol.CurrentVersion,
        migration.PlanId,
        migration.SpaceId,
        migration.Epoch,
        migration.InitiatorDeviceId,
        source.Endpoint.AbsoluteUri,
        source.RemoteRoot,
        source.CertificateSha256Pin,
        source.AllowInsecureLoopback,
        migration.SourceRemoteFingerprint,
        target.Endpoint.AbsoluteUri,
        target.RemoteRoot,
        target.CertificateSha256Pin,
        target.AllowInsecureLoopback,
        migration.TargetRemoteFingerprint,
        requiredDeviceIds,
        migration.CreatedAtUnixMilliseconds);

    private static SyncProviderMigrationDeviceMarker CreateDeviceMarker(
        SyncProviderMigrationIntent intent,
        SyncProviderMigrationMarkerKind kind,
        Guid deviceId,
        SyncProviderMigrationWatermark watermark) => new(
        SyncProviderMigrationProtocol.CurrentVersion,
        kind,
        intent.PlanId,
        intent.SpaceId,
        intent.Epoch,
        deviceId,
        watermark.HighestLocalSequence,
        watermark.HighestUploadedSequence,
        watermark.Checkpoints
            .OrderBy(static checkpoint => checkpoint.DeviceId.ToString("N"), StringComparer.Ordinal)
            .Select(static checkpoint => new SyncProviderMigrationCheckpoint(
                checkpoint.DeviceId,
                checkpoint.AppliedSequence,
                checkpoint.AppliedEventId,
                checkpoint.ETag))
            .ToArray(),
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static SyncProviderMigrationDecision CreateDecision(
        SyncProviderMigrationIntent intent,
        SyncProviderMigrationMarkerKind kind,
        SyncProviderMigrationRecord migration)
    {
        bool bindsInventory = kind is SyncProviderMigrationMarkerKind.Commit or
            SyncProviderMigrationMarkerKind.Completed;
        return new SyncProviderMigrationDecision(
            SyncProviderMigrationProtocol.CurrentVersion,
            kind,
            intent.PlanId,
            intent.SpaceId,
            intent.Epoch,
            intent.InitiatorDeviceId,
            bindsInventory ? migration.TotalObjects : 0,
            bindsInventory ? migration.TotalBytes : 0,
            bindsInventory ? migration.InventorySha256 : null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            bindsInventory ? migration.DiagnosticCode : null);
    }

    private static SyncObjectDescriptor CreateMigrationDescriptor(
        SyncConfigurationSnapshot configuration,
        Guid planId,
        long epoch,
        Guid ownerDeviceId) => new(
        SyncProtocol.CurrentVersion,
        configuration.SpaceId,
        ownerDeviceId,
        SyncObjectType.ProviderMigration,
        epoch,
        planId.ToString("N"),
        configuration.KeyVersion);

    private static async ValueTask EnsureMigrationHierarchiesAsync(
        ISyncRemoteProviderMigrationSession source,
        ISyncRemoteProviderMigrationSession target,
        Guid spaceId,
        Guid planId,
        IReadOnlyList<Guid> requiredDeviceIds,
        CancellationToken cancellationToken)
    {
        SyncRemoteResult sourceResult = await source.EnsureMigrationHierarchyAsync(
                spaceId,
                planId,
                requiredDeviceIds,
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(sourceResult, "provider-migration-source-preflight-failed");
        SyncRemoteResult targetResult = await target.EnsureMigrationHierarchyAsync(
                spaceId,
                planId,
                requiredDeviceIds,
                cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRemoteFailure(targetResult, "provider-migration-target-preflight-failed");
    }

    private async ValueTask<SyncProviderMigrationIntent?> TryReadLocalMigrationIntentAsync(
        SyncConfigurationSnapshot configuration,
        SyncProviderMigrationRecord migration,
        CancellationToken cancellationToken)
    {
        if (_providerMigrationSessionFactory is null)
        {
            return null;
        }

        SyncMasterKeyOpenResult keyResult = await _keyService.OpenMasterKeyAsync(
                configuration.SpaceId,
                configuration.KeyVersion,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureMasterKeyAvailable(keyResult);
        SyncCredentialOpenResult credential = await _credentialService.OpenMigrationAsync(
                configuration.SpaceId,
                migration.PlanId,
                SyncMigrationCredentialSlot.Source,
                cancellationToken)
            .ConfigureAwait(false);
        if (credential.Status == SyncCredentialOperationStatus.NotFound)
        {
            credential = await _credentialService.OpenAsync(
                    configuration.SpaceId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        EnsureCredentialAvailable(credential, "provider-migration-source-credential-unavailable");
        using (keyResult.Key!)
        using (credential.Credential!)
        await using (ISyncRemoteProviderMigrationSession session =
            _providerMigrationSessionFactory.CreateProviderMigrationSession(
                credential.Credential!.RemoteConfiguration,
                credential.Credential.Password))
        {
            return await ReadProviderMigrationIntentAsync(
                    session,
                    configuration,
                    migration.PlanId,
                    keyResult.Key!.Key,
                    allowNotFound: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void ValidateProviderMigrationIntent(
        SyncProviderMigrationIntent intent,
        Guid spaceId,
        Guid planId)
    {
        if (intent.MigrationProtocolVersion != SyncProviderMigrationProtocol.CurrentVersion ||
            intent.PlanId != planId || intent.SpaceId != spaceId || intent.Epoch <= 0 ||
            intent.InitiatorDeviceId == Guid.Empty ||
            intent.RequiredDeviceIds is null ||
            intent.RequiredDeviceIds.Length is < 1 or >
                SyncProviderMigrationProtocol.MaximumDevices ||
            intent.RequiredDeviceIds.Any(static deviceId => deviceId == Guid.Empty) ||
            intent.RequiredDeviceIds.Distinct().Count() != intent.RequiredDeviceIds.Length ||
            !intent.RequiredDeviceIds.Contains(intent.InitiatorDeviceId) ||
            !intent.RequiredDeviceIds.SequenceEqual(
                intent.RequiredDeviceIds.OrderBy(
                    static deviceId => deviceId.ToString("N"),
                    StringComparer.Ordinal)) ||
            intent.CreatedAtUnixMilliseconds <= 0)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-intent-invalid");
        }

        try
        {
            _ = DateTimeOffset.FromUnixTimeMilliseconds(intent.CreatedAtUnixMilliseconds);
            SyncRemoteConfiguration source = CreateIntentRemoteConfiguration(
                intent.SourceEndpoint,
                intent.SourceRemoteRoot,
                intent.SourceCertificateSha256Pin,
                intent.SourceAllowInsecureLoopback,
                username: string.Empty);
            SyncRemoteConfiguration target = CreateIntentRemoteConfiguration(
                intent.TargetEndpoint,
                intent.TargetRemoteRoot,
                intent.TargetCertificateSha256Pin,
                intent.TargetAllowInsecureLoopback,
                username: string.Empty);
            if (!string.Equals(
                    ComputeRemoteFingerprint(source),
                    intent.SourceRemoteFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ComputeRemoteFingerprint(target),
                    intent.TargetRemoteFingerprint,
                    StringComparison.Ordinal) ||
                string.Equals(
                    intent.SourceRemoteFingerprint,
                    intent.TargetRemoteFingerprint,
                    StringComparison.Ordinal))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    "provider-migration-intent-fingerprint-invalid");
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or UriFormatException or ArgumentOutOfRangeException)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-intent-remote-invalid");
        }
    }

    private static void ValidateDeviceMarker(
        SyncProviderMigrationDeviceMarker marker,
        SyncProviderMigrationIntent intent,
        SyncProviderMigrationMarkerKind kind,
        Guid deviceId)
    {
        if (marker.MigrationProtocolVersion != SyncProviderMigrationProtocol.CurrentVersion ||
            marker.Kind != kind || marker.PlanId != intent.PlanId ||
            marker.SpaceId != intent.SpaceId || marker.Epoch != intent.Epoch ||
            marker.DeviceId != deviceId || !intent.RequiredDeviceIds.Contains(deviceId) ||
            marker.HighestLocalSequence < 0 || marker.HighestUploadedSequence < 0 ||
            marker.HighestUploadedSequence > marker.HighestLocalSequence ||
            marker.Checkpoints is null ||
            marker.Checkpoints.Length > SyncProviderMigrationProtocol.MaximumDevices ||
            marker.Checkpoints.Any(static checkpoint =>
                checkpoint.DeviceId == Guid.Empty || checkpoint.AppliedSequence < 0 ||
                checkpoint.AppliedSequence == 0 && checkpoint.AppliedEventId is not null ||
                checkpoint.ETag is { Length: > 256 } ||
                checkpoint.ETag?.Any(char.IsControl) == true) ||
            marker.Checkpoints.Select(static checkpoint => checkpoint.DeviceId).Distinct().Count() !=
                marker.Checkpoints.Length ||
            !marker.Checkpoints.SequenceEqual(marker.Checkpoints.OrderBy(
                static checkpoint => checkpoint.DeviceId.ToString("N"),
                StringComparer.Ordinal)) ||
            marker.CreatedAtUnixMilliseconds <= 0 ||
            !IsValidProviderMigrationDiagnostic(marker.DiagnosticCode))
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-device-marker-invalid");
        }
    }

    private static void ValidateDecision(
        SyncProviderMigrationDecision decision,
        SyncProviderMigrationIntent intent,
        SyncProviderMigrationMarkerKind kind)
    {
        if (decision.MigrationProtocolVersion != SyncProviderMigrationProtocol.CurrentVersion ||
            decision.Kind != kind || decision.PlanId != intent.PlanId ||
            decision.SpaceId != intent.SpaceId || decision.Epoch != intent.Epoch ||
            decision.InitiatorDeviceId != intent.InitiatorDeviceId ||
            decision.ObjectCount < 0 || decision.TotalBytes < 0 ||
            decision.InventorySha256 is not null &&
                !SyncRemoteLayout.IsLowerHex(decision.InventorySha256, 64) ||
            kind is SyncProviderMigrationMarkerKind.Commit or
                SyncProviderMigrationMarkerKind.Completed &&
                (decision.ObjectCount < 1 || decision.InventorySha256 is null) ||
            decision.CreatedAtUnixMilliseconds <= 0 ||
            !IsValidProviderMigrationDiagnostic(decision.DiagnosticCode))
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-decision-invalid");
        }
    }

    private static void ValidateStoredMigrationAgainstIntent(
        SyncProviderMigrationRecord migration,
        SyncProviderMigrationIntent intent)
    {
        if (migration.PlanId != intent.PlanId || migration.SpaceId != intent.SpaceId ||
            migration.Epoch != intent.Epoch ||
            migration.InitiatorDeviceId != intent.InitiatorDeviceId ||
            !string.Equals(
                migration.SourceRemoteFingerprint,
                intent.SourceRemoteFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                migration.TargetRemoteFingerprint,
                intent.TargetRemoteFingerprint,
                StringComparison.Ordinal))
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-local-state-mismatch");
        }
    }

    private static void ValidateCommitAgainstMigration(
        SyncProviderMigrationDecision commit,
        SyncProviderMigrationRecord migration)
    {
        if (migration.InventorySha256 is not null &&
            (commit.ObjectCount != migration.TotalObjects ||
                commit.TotalBytes != migration.TotalBytes ||
                !string.Equals(
                    commit.InventorySha256,
                    migration.InventorySha256,
                    StringComparison.Ordinal)))
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "provider-migration-commit-inventory-mismatch");
        }
    }

    private static bool TargetConfigurationMatchesIntent(
        SyncRemoteConfiguration configuration,
        SyncProviderMigrationIntent intent) =>
        string.Equals(
            configuration.Endpoint.AbsoluteUri,
            intent.TargetEndpoint,
            StringComparison.Ordinal) &&
        string.Equals(configuration.RemoteRoot, intent.TargetRemoteRoot, StringComparison.Ordinal) &&
        string.Equals(
            configuration.CertificateSha256Pin,
            intent.TargetCertificateSha256Pin,
            StringComparison.Ordinal) &&
        configuration.AllowInsecureLoopback == intent.TargetAllowInsecureLoopback &&
        string.Equals(
            ComputeRemoteFingerprint(configuration),
            intent.TargetRemoteFingerprint,
            StringComparison.Ordinal);

    private static string ComputeRemoteFingerprint(SyncRemoteConfiguration configuration)
    {
        string canonical = string.Join(
            '\0',
            configuration.Endpoint.AbsoluteUri,
            configuration.RemoteRoot,
            configuration.CertificateSha256Pin ?? string.Empty,
            configuration.AllowInsecureLoopback ? "1" : "0");
        byte[] bytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static SyncRemoteConfiguration CreateIntentRemoteConfiguration(
        string endpoint,
        string remoteRoot,
        string? certificateSha256Pin,
        bool allowInsecureLoopback,
        string username) => new(
        new Uri(endpoint, UriKind.Absolute),
        remoteRoot,
        username,
        certificateSha256Pin,
        allowInsecureLoopback);

    private static void EnsureMasterKeyAvailable(SyncMasterKeyOpenResult result)
    {
        if (result.Status != SyncKeyOperationStatus.Success || result.Key is null)
        {
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                "master-key-unavailable",
                keyUnavailable: true);
        }
    }

    private static void EnsureCredentialAvailable(
        SyncCredentialOpenResult result,
        string diagnosticCode)
    {
        if (result.Status != SyncCredentialOperationStatus.Success || result.Credential is null)
        {
            throw new SyncPipelineException(
                result.Status == SyncCredentialOperationStatus.AccessDenied
                    ? SyncRemoteErrorCategory.Permission
                    : result.Status == SyncCredentialOperationStatus.Conflict
                        ? SyncRemoteErrorCategory.Protocol
                        : SyncRemoteErrorCategory.Authentication,
                diagnosticCode);
        }
    }

    private static void EnsureCredentialOperationSucceeded(
        SyncCredentialOperationStatus status,
        string diagnosticCode)
    {
        if (status != SyncCredentialOperationStatus.Success)
        {
            throw new SyncPipelineException(
                status == SyncCredentialOperationStatus.AccessDenied
                    ? SyncRemoteErrorCategory.Permission
                    : status == SyncCredentialOperationStatus.Conflict
                        ? SyncRemoteErrorCategory.Protocol
                        : SyncRemoteErrorCategory.Authentication,
                diagnosticCode);
        }
    }

    private static T DeserializeCanonical<T>(
        ReadOnlySpan<byte> plaintext,
        JsonTypeInfo<T> typeInfo,
        string diagnosticCode)
    {
        T value = JsonSerializer.Deserialize(plaintext, typeInfo) ??
            throw new SyncPipelineException(
                SyncRemoteErrorCategory.Protocol,
                diagnosticCode);
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        try
        {
            if (!canonical.AsSpan().SequenceEqual(plaintext))
            {
                throw new SyncPipelineException(
                    SyncRemoteErrorCategory.Protocol,
                    diagnosticCode);
            }

            return value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static bool ProviderMigrationIntentsEqual(
        SyncProviderMigrationIntent left,
        SyncProviderMigrationIntent right) =>
        left with { RequiredDeviceIds = [] } == right with { RequiredDeviceIds = [] } &&
        left.RequiredDeviceIds.SequenceEqual(right.RequiredDeviceIds);

    private static bool DeviceMarkersEqual(
        SyncProviderMigrationDeviceMarker left,
        SyncProviderMigrationDeviceMarker right) =>
        left with { Checkpoints = [], CreatedAtUnixMilliseconds = 0 } ==
            right with { Checkpoints = [], CreatedAtUnixMilliseconds = 0 } &&
        left.Checkpoints.SequenceEqual(right.Checkpoints);

    private static bool DecisionsEqual(
        SyncProviderMigrationDecision left,
        SyncProviderMigrationDecision right) =>
        left with { CreatedAtUnixMilliseconds = 0 } ==
            right with { CreatedAtUnixMilliseconds = 0 };

    private static bool IsValidProviderMigrationDiagnostic(string? value) =>
        value is null || value.Length is >= 1 and <= 128 && !value.Any(char.IsControl);

    private sealed record RemoteProviderMigrationScan(
        SyncProviderMigrationIntent? LatestIntent,
        long HighestEpoch,
        bool LatestRolledBack,
        bool LatestCompleted)
    {
        public bool LatestIsTerminal => LatestRolledBack || LatestCompleted;
    }

    private sealed record CiphertextInventoryEntry(
        string Identity,
        int Length,
        string Sha256);
}
