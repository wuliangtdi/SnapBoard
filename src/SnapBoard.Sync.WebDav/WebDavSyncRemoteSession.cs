using System.Net;
using System.Text;
using SnapBoard.Application.Sync;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Sync.WebDav;

public sealed class WebDavSyncRemoteSessionFactory :
    ISyncRemoteSessionFactory,
    ISyncRemoteProviderMigrationSessionFactory
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public ISyncRemoteSession Create(
        SyncRemoteConfiguration configuration,
        ReadOnlyMemory<byte> password) => CreateCore(configuration, password);

    public ISyncRemoteProviderMigrationSession CreateProviderMigrationSession(
        SyncRemoteConfiguration configuration,
        ReadOnlyMemory<byte> password) => CreateCore(configuration, password);

    private static WebDavSyncRemoteSession CreateCore(
        SyncRemoteConfiguration configuration,
        ReadOnlyMemory<byte> password)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string passwordText;
        try
        {
            passwordText = StrictUtf8.GetString(password.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("The WebDAV password is not valid UTF-8.", nameof(password), exception);
        }

        WebDavOptions options = new(
            configuration.Endpoint,
            configuration.RemoteRoot,
            configuration.AllowInsecureLoopback,
            configuration.CertificateSha256Pin);
        NetworkCredential? credential =
            configuration.Username.Length == 0 && password.Length == 0
                ? null
                : new NetworkCredential(configuration.Username, passwordText);
        HttpClient httpClient = WebDavHttpClientFactory.Create(options, credential);
        return new WebDavSyncRemoteSession(
            new WebDavClient(httpClient, options, disposeHttpClient: true));
    }
}

public sealed class WebDavSyncRemoteSession :
    ISyncRemoteSession,
    ISyncRemoteProviderMigrationSession
{
    private readonly WebDavClient _client;
    private int _disposed;

    public WebDavSyncRemoteSession(WebDavClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async ValueTask<SyncRemoteResult> EnsureHierarchyAsync(
        Guid spaceId,
        Guid localDeviceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string deviceRoot = GetDeviceRoot(spaceId, localDeviceId);
        string[] collections =
        [
            $"{deviceRoot}/events",
            $"{deviceRoot}/checkpoints",
            $"{GetSpaceRoot(spaceId)}/blobs",
        ];
        foreach (string collection in collections)
        {
            WebDavResult result = await _client.EnsureCollectionAsync(
                    collection,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncRemoteResult mapped = Map(result);
            if (!mapped.IsSuccess)
            {
                return mapped;
            }
        }

        return Success();
    }

    public ValueTask<SyncRemoteContentResult> GetMetadataAsync(
        Guid spaceId,
        CancellationToken cancellationToken) => GetAsync(
        $"{GetSpaceRoot(spaceId)}/metadata.enc",
        cancellationToken);

    public ValueTask<SyncRemoteResult> PutMetadataAsync(
        Guid spaceId,
        ReadOnlyMemory<byte> encryptedMetadata,
        CancellationToken cancellationToken) => PutAsync(
        $"{GetSpaceRoot(spaceId)}/metadata.enc",
        encryptedMetadata,
        cancellationToken);

    public async ValueTask<SyncRemoteDeviceListResult> ListDevicesAsync(
        Guid spaceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string collection = $"{GetSpaceRoot(spaceId)}/devices";
        WebDavListResult list = await _client.ListAsync(collection, cancellationToken)
            .ConfigureAwait(false);
        SyncRemoteResult mapped = Map(list.Result);
        if (!mapped.IsSuccess)
        {
            return new SyncRemoteDeviceListResult(mapped, []);
        }

        List<Guid> devices = [];
        foreach (WebDavResource resource in list.Resources)
        {
            if (IsCollectionSelf(resource, collection))
            {
                continue;
            }

            if (!IsImmediateChild(resource.RelativePath, collection) ||
                !resource.IsCollection ||
                !Guid.TryParseExact(resource.ObjectName, "N", out Guid deviceId) ||
                deviceId == Guid.Empty ||
                !string.Equals(deviceId.ToString("N"), resource.ObjectName, StringComparison.Ordinal) ||
                devices.Contains(deviceId))
            {
                return new SyncRemoteDeviceListResult(ProtocolFailure(), []);
            }

            devices.Add(deviceId);
        }

        devices.Sort(static (left, right) => string.Compare(
            left.ToString("N"),
            right.ToString("N"),
            StringComparison.Ordinal));
        return new SyncRemoteDeviceListResult(mapped, devices);
    }

    public async ValueTask<SyncRemoteEventListResult> ListEventsAsync(
        Guid spaceId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string collection = $"{GetDeviceRoot(spaceId, deviceId)}/events";
        WebDavListResult list = await _client.ListAsync(collection, cancellationToken)
            .ConfigureAwait(false);
        SyncRemoteResult mapped = Map(list.Result);
        if (!mapped.IsSuccess)
        {
            return new SyncRemoteEventListResult(mapped, []);
        }

        List<SyncRemoteEventReference> events = [];
        foreach (WebDavResource resource in list.Resources)
        {
            if (IsCollectionSelf(resource, collection))
            {
                continue;
            }

            if (!IsImmediateChild(resource.RelativePath, collection) ||
                resource.IsCollection ||
                !SyncRemoteLayout.TryParseEventObjectName(
                    resource.ObjectName,
                    out long sequence,
                    out Guid eventId))
            {
                return new SyncRemoteEventListResult(ProtocolFailure(), []);
            }

            events.Add(new SyncRemoteEventReference(
                deviceId,
                sequence,
                eventId,
                resource.ETag));
        }

        events.Sort(static (left, right) =>
        {
            int sequence = left.Sequence.CompareTo(right.Sequence);
            return sequence != 0
                ? sequence
                : string.Compare(
                    left.EventId.ToString("N"),
                    right.EventId.ToString("N"),
                    StringComparison.Ordinal);
        });
        for (int index = 1; index < events.Count; index++)
        {
            if (events[index - 1].Sequence == events[index].Sequence ||
                events[index - 1].EventId == events[index].EventId)
            {
                return new SyncRemoteEventListResult(ProtocolFailure(), []);
            }
        }

        return new SyncRemoteEventListResult(mapped, events);
    }

    public ValueTask<SyncRemoteContentResult> GetEventAsync(
        Guid spaceId,
        SyncRemoteEventReference remoteEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteEvent);
        return GetAsync(
            GetEventPath(
                spaceId,
                remoteEvent.DeviceId,
                remoteEvent.Sequence,
                remoteEvent.EventId),
            cancellationToken);
    }

    public ValueTask<SyncRemoteResult> PutEventAsync(
        Guid spaceId,
        Guid deviceId,
        long sequence,
        Guid eventId,
        ReadOnlyMemory<byte> encryptedEvent,
        CancellationToken cancellationToken) => PutAsync(
        GetEventPath(spaceId, deviceId, sequence, eventId),
        encryptedEvent,
        cancellationToken);

    public ValueTask<SyncRemoteContentResult> GetBlobAsync(
        Guid spaceId,
        string keyedBlobId,
        CancellationToken cancellationToken) => GetAsync(
        GetBlobPath(spaceId, keyedBlobId),
        cancellationToken);

    public ValueTask<SyncRemoteResult> PutBlobAsync(
        Guid spaceId,
        string keyedBlobId,
        ReadOnlyMemory<byte> encryptedBlob,
        CancellationToken cancellationToken) => PutAsync(
        GetBlobPath(spaceId, keyedBlobId),
        encryptedBlob,
        cancellationToken);

    public async ValueTask<SyncRemoteResult> EnsureMigrationHierarchyAsync(
        Guid spaceId,
        Guid planId,
        IReadOnlyList<Guid> requiredDeviceIds,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfEqual(spaceId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(planId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(requiredDeviceIds);
        if (requiredDeviceIds.Count is < 1 or > SyncProviderMigrationProtocol.MaximumDevices ||
            requiredDeviceIds.Any(static deviceId => deviceId == Guid.Empty) ||
            requiredDeviceIds.Distinct().Count() != requiredDeviceIds.Count)
        {
            throw new ArgumentException("The required migration device set is invalid.", nameof(requiredDeviceIds));
        }

        foreach (Guid deviceId in requiredDeviceIds)
        {
            SyncRemoteResult hierarchy = await EnsureHierarchyAsync(
                    spaceId,
                    deviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!hierarchy.IsSuccess)
            {
                return hierarchy;
            }
        }

        string planRoot = GetProviderMigrationRoot(spaceId, planId);
        string[] collections =
        [
            GetProviderMigrationsCollection(spaceId),
            planRoot,
            $"{planRoot}/ready",
            $"{planRoot}/committed",
            $"{planRoot}/rolled-back",
        ];
        foreach (string collection in collections)
        {
            SyncRemoteResult result = Map(await _client.EnsureCollectionAsync(
                    collection,
                    cancellationToken)
                .ConfigureAwait(false));
            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return Success();
    }

    public async ValueTask<SyncRemoteCiphertextObjectListResult> ListCiphertextObjectsAsync(
        Guid spaceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfEqual(spaceId, Guid.Empty);
        List<SyncRemoteCiphertextObjectReference> objects = [];
        SyncRemoteContentResult metadata = await GetMetadataAsync(spaceId, cancellationToken)
            .ConfigureAwait(false);
        if (metadata.Result.IsSuccess && metadata.Content is not null)
        {
            using (metadata.Content)
            {
                objects.Add(new SyncRemoteCiphertextObjectReference(
                    SyncObjectType.Metadata,
                    DeviceId: null,
                    Sequence: 0,
                    EventId: null,
                    KeyedBlobId: null,
                    metadata.Result.ETag,
                    metadata.Content.Content.Length));
            }
        }
        else if (metadata.Result.ErrorCategory != SyncRemoteErrorCategory.NotFound)
        {
            return new SyncRemoteCiphertextObjectListResult(metadata.Result, []);
        }

        SyncRemoteDeviceListResult devices = await ListDevicesAsync(
                spaceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!devices.Result.IsSuccess)
        {
            return new SyncRemoteCiphertextObjectListResult(devices.Result, []);
        }

        foreach (Guid deviceId in devices.DeviceIds)
        {
            SyncRemoteEventListResult events = await ListEventsAsync(
                    spaceId,
                    deviceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!events.Result.IsSuccess)
            {
                return new SyncRemoteCiphertextObjectListResult(events.Result, []);
            }

            objects.AddRange(events.Events.Select(remoteEvent =>
                new SyncRemoteCiphertextObjectReference(
                    SyncObjectType.Event,
                    remoteEvent.DeviceId,
                    remoteEvent.Sequence,
                    remoteEvent.EventId,
                    KeyedBlobId: null,
                    remoteEvent.ETag,
                    ContentLength: null)));

            string checkpoints = $"{GetDeviceRoot(spaceId, deviceId)}/checkpoints";
            WebDavListResult checkpointList = await _client.ListAsync(
                    checkpoints,
                    cancellationToken)
                .ConfigureAwait(false);
            SyncRemoteResult checkpointResult = Map(checkpointList.Result);
            if (!checkpointResult.IsSuccess)
            {
                return new SyncRemoteCiphertextObjectListResult(checkpointResult, []);
            }

            if (checkpointList.Resources.Any(resource => !IsCollectionSelf(resource, checkpoints)))
            {
                return new SyncRemoteCiphertextObjectListResult(ProtocolFailure(), []);
            }
        }

        string blobs = $"{GetSpaceRoot(spaceId)}/blobs";
        WebDavListResult blobList = await _client.ListAsync(blobs, cancellationToken)
            .ConfigureAwait(false);
        SyncRemoteResult blobResult = Map(blobList.Result);
        if (!blobResult.IsSuccess)
        {
            return new SyncRemoteCiphertextObjectListResult(blobResult, []);
        }

        HashSet<string> blobIds = new(StringComparer.Ordinal);
        foreach (WebDavResource resource in blobList.Resources)
        {
            if (IsCollectionSelf(resource, blobs))
            {
                continue;
            }

            if (!IsImmediateChild(resource.RelativePath, blobs) || resource.IsCollection ||
                !SyncRemoteLayout.TryParseBlobObjectName(
                    resource.ObjectName,
                    out string keyedBlobId) ||
                !blobIds.Add(keyedBlobId))
            {
                return new SyncRemoteCiphertextObjectListResult(ProtocolFailure(), []);
            }

            objects.Add(new SyncRemoteCiphertextObjectReference(
                SyncObjectType.Blob,
                DeviceId: null,
                Sequence: 0,
                EventId: null,
                keyedBlobId,
                resource.ETag,
                resource.ContentLength));
        }

        objects.Sort(CompareCiphertextReferences);
        return new SyncRemoteCiphertextObjectListResult(Success(), objects);
    }

    public ValueTask<SyncRemoteContentResult> GetCiphertextObjectAsync(
        Guid spaceId,
        SyncRemoteCiphertextObjectReference reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.ObjectType switch
        {
            SyncObjectType.Metadata when reference.DeviceId is null &&
                reference.Sequence == 0 && reference.EventId is null &&
                reference.KeyedBlobId is null => GetMetadataAsync(spaceId, cancellationToken),
            SyncObjectType.Event when reference.DeviceId is not null &&
                reference.Sequence > 0 && reference.EventId is not null &&
                reference.KeyedBlobId is null => GetEventAsync(
                    spaceId,
                    new SyncRemoteEventReference(
                        reference.DeviceId.Value,
                        reference.Sequence,
                        reference.EventId.Value,
                        reference.ETag),
                    cancellationToken),
            SyncObjectType.Blob when reference.DeviceId is null &&
                reference.Sequence == 0 && reference.EventId is null &&
                reference.KeyedBlobId is not null => GetBlobAsync(
                    spaceId,
                    reference.KeyedBlobId,
                    cancellationToken),
            _ => ValueTask.FromResult(new SyncRemoteContentResult(ProtocolFailure())),
        };
    }

    public ValueTask<SyncRemoteResult> PutCiphertextObjectAsync(
        Guid spaceId,
        SyncRemoteCiphertextObjectReference reference,
        ReadOnlyMemory<byte> encryptedContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.ObjectType switch
        {
            SyncObjectType.Metadata when reference.DeviceId is null &&
                reference.Sequence == 0 && reference.EventId is null &&
                reference.KeyedBlobId is null => PutMetadataAsync(
                    spaceId,
                    encryptedContent,
                    cancellationToken),
            SyncObjectType.Event when reference.DeviceId is not null &&
                reference.Sequence > 0 && reference.EventId is not null &&
                reference.KeyedBlobId is null => PutEventAsync(
                    spaceId,
                    reference.DeviceId.Value,
                    reference.Sequence,
                    reference.EventId.Value,
                    encryptedContent,
                    cancellationToken),
            SyncObjectType.Blob when reference.DeviceId is null &&
                reference.Sequence == 0 && reference.EventId is null &&
                reference.KeyedBlobId is not null => PutBlobAsync(
                    spaceId,
                    reference.KeyedBlobId,
                    encryptedContent,
                    cancellationToken),
            _ => ValueTask.FromResult(ProtocolFailure()),
        };
    }

    public async ValueTask<SyncRemoteProviderMigrationPlanListResult>
        ListProviderMigrationPlansAsync(
            Guid spaceId,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string collection = GetProviderMigrationsCollection(spaceId);
        WebDavListResult list = await _client.ListAsync(collection, cancellationToken)
            .ConfigureAwait(false);
        SyncRemoteResult mapped = Map(list.Result);
        if (!mapped.IsSuccess)
        {
            return mapped.ErrorCategory == SyncRemoteErrorCategory.NotFound
                ? new SyncRemoteProviderMigrationPlanListResult(Success(), [])
                : new SyncRemoteProviderMigrationPlanListResult(mapped, []);
        }

        List<SyncRemoteProviderMigrationPlanReference> plans = [];
        foreach (WebDavResource resource in list.Resources)
        {
            if (IsCollectionSelf(resource, collection))
            {
                continue;
            }

            if (!IsImmediateChild(resource.RelativePath, collection) || !resource.IsCollection ||
                !Guid.TryParseExact(resource.ObjectName, "N", out Guid planId) ||
                planId == Guid.Empty ||
                !string.Equals(planId.ToString("N"), resource.ObjectName, StringComparison.Ordinal) ||
                plans.Any(plan => plan.PlanId == planId))
            {
                return new SyncRemoteProviderMigrationPlanListResult(ProtocolFailure(), []);
            }

            plans.Add(new SyncRemoteProviderMigrationPlanReference(planId, resource.ETag));
        }

        plans.Sort(static (left, right) => string.Compare(
            left.PlanId.ToString("N"),
            right.PlanId.ToString("N"),
            StringComparison.Ordinal));
        return new SyncRemoteProviderMigrationPlanListResult(Success(), plans);
    }

    public ValueTask<SyncRemoteContentResult> GetProviderMigrationMarkerAsync(
        Guid spaceId,
        SyncProviderMigrationMarkerAddress address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        return GetAsync(GetProviderMigrationMarkerPath(spaceId, address), cancellationToken);
    }

    public ValueTask<SyncRemoteResult> PutProviderMigrationMarkerAsync(
        Guid spaceId,
        SyncProviderMigrationMarkerAddress address,
        ReadOnlyMemory<byte> encryptedMarker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        return PutAsync(
            GetProviderMigrationMarkerPath(spaceId, address),
            encryptedMarker,
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<SyncRemoteContentResult> GetAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        WebDavContentResult result = await _client.GetAsync(
                relativePath,
                SyncProtocol.MaximumEncryptedEnvelopeBytes,
                ifNoneMatch: null,
                cancellationToken)
            .ConfigureAwait(false);
        SyncRemoteResult mapped = Map(result.Result);
        if (!mapped.IsSuccess || result.Content is null)
        {
            return new SyncRemoteContentResult(mapped);
        }

        return new SyncRemoteContentResult(
            mapped,
            new SyncRemoteContentLease(result.Content));
    }

    private async ValueTask<SyncRemoteResult> PutAsync(
        string relativePath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        WebDavResult result = await _client.PutImmutableAsync(
                relativePath,
                content,
                "application/vnd.snapboard.encrypted+json",
                cancellationToken)
            .ConfigureAwait(false);
        return Map(result, immutableWrite: true);
    }

    private static SyncRemoteResult Map(
        WebDavResult result,
        bool immutableWrite = false)
    {
        if (result.IsSuccess)
        {
            return new SyncRemoteResult(
                true,
                SyncRemoteErrorCategory.None,
                result.ETag,
                result.RetryAfter);
        }

        if (immutableWrite && result.ErrorCategory == WebDavErrorCategory.PreconditionFailed)
        {
            return new SyncRemoteResult(
                true,
                SyncRemoteErrorCategory.None,
                result.ETag,
                result.RetryAfter,
                AlreadyExisted: true);
        }

        SyncRemoteErrorCategory category = result.ErrorCategory switch
        {
            WebDavErrorCategory.Authentication => SyncRemoteErrorCategory.Authentication,
            WebDavErrorCategory.Permission => SyncRemoteErrorCategory.Permission,
            WebDavErrorCategory.NotFound => SyncRemoteErrorCategory.NotFound,
            WebDavErrorCategory.RateLimited => SyncRemoteErrorCategory.RateLimited,
            WebDavErrorCategory.Locked or WebDavErrorCategory.TransientServer or
                WebDavErrorCategory.Conflict => SyncRemoteErrorCategory.Transient,
            WebDavErrorCategory.Network => SyncRemoteErrorCategory.Network,
            WebDavErrorCategory.Timeout => SyncRemoteErrorCategory.Timeout,
            WebDavErrorCategory.Certificate => SyncRemoteErrorCategory.Certificate,
            WebDavErrorCategory.ResponseTooLarge => SyncRemoteErrorCategory.ResponseTooLarge,
            _ => SyncRemoteErrorCategory.Protocol,
        };
        return new SyncRemoteResult(
            false,
            category,
            result.ETag,
            result.RetryAfter);
    }

    private static SyncRemoteResult Success() =>
        new(true, SyncRemoteErrorCategory.None);

    private static SyncRemoteResult ProtocolFailure() =>
        new(false, SyncRemoteErrorCategory.Protocol);

    private static bool IsCollectionSelf(WebDavResource resource, string collection) =>
        resource.IsCollection &&
        string.Equals(resource.RelativePath, collection, StringComparison.Ordinal);

    private static bool IsImmediateChild(string path, string collection)
    {
        string prefix = collection + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal) &&
            !path.AsSpan(prefix.Length).Contains('/');
    }

    private static int CompareCiphertextReferences(
        SyncRemoteCiphertextObjectReference left,
        SyncRemoteCiphertextObjectReference right)
    {
        int type = left.ObjectType.CompareTo(right.ObjectType);
        if (type != 0)
        {
            return type;
        }

        int device = string.Compare(
            left.DeviceId?.ToString("N"),
            right.DeviceId?.ToString("N"),
            StringComparison.Ordinal);
        if (device != 0)
        {
            return device;
        }

        int sequence = left.Sequence.CompareTo(right.Sequence);
        if (sequence != 0)
        {
            return sequence;
        }

        int eventId = string.Compare(
            left.EventId?.ToString("N"),
            right.EventId?.ToString("N"),
            StringComparison.Ordinal);
        return eventId != 0
            ? eventId
            : string.Compare(left.KeyedBlobId, right.KeyedBlobId, StringComparison.Ordinal);
    }

    private static string GetSpaceRoot(Guid spaceId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(spaceId, Guid.Empty);
        return $"spaces/{spaceId:N}";
    }

    private static string GetDeviceRoot(Guid spaceId, Guid deviceId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(deviceId, Guid.Empty);
        return $"{GetSpaceRoot(spaceId)}/devices/{deviceId:N}";
    }

    private static string GetProviderMigrationsCollection(Guid spaceId) =>
        RemoveProtocolRoot(SyncRemoteLayout.GetProviderMigrationsCollection(spaceId));

    private static string GetProviderMigrationRoot(Guid spaceId, Guid planId) =>
        RemoveProtocolRoot(SyncRemoteLayout.GetProviderMigrationRoot(spaceId, planId));

    private static string GetProviderMigrationMarkerPath(
        Guid spaceId,
        SyncProviderMigrationMarkerAddress address)
    {
        string canonical = address.Kind switch
        {
            SyncProviderMigrationMarkerKind.Intent when address.DeviceId is null =>
                SyncRemoteLayout.GetProviderMigrationIntentPath(spaceId, address.PlanId),
            SyncProviderMigrationMarkerKind.Ready or
                SyncProviderMigrationMarkerKind.Committed or
                SyncProviderMigrationMarkerKind.RolledBack when address.DeviceId is not null =>
                SyncRemoteLayout.GetProviderMigrationDeviceMarkerPath(
                    spaceId,
                    address.PlanId,
                    address.Kind,
                    address.DeviceId.Value),
            SyncProviderMigrationMarkerKind.Freeze or
                SyncProviderMigrationMarkerKind.Commit or
                SyncProviderMigrationMarkerKind.Rollback or
                SyncProviderMigrationMarkerKind.Completed when address.DeviceId is null =>
                SyncRemoteLayout.GetProviderMigrationDecisionPath(
                    spaceId,
                    address.PlanId,
                    address.Kind),
            _ => throw new ArgumentException(
                "The provider migration marker address is invalid.",
                nameof(address)),
        };
        return RemoveProtocolRoot(canonical);
    }

    private static string GetEventPath(
        Guid spaceId,
        Guid deviceId,
        long sequence,
        Guid eventId)
    {
        string canonical = SyncRemoteLayout.GetEventPath(
            spaceId,
            deviceId,
            sequence,
            eventId);
        return RemoveProtocolRoot(canonical);
    }

    private static string GetBlobPath(Guid spaceId, string keyedBlobId)
    {
        string canonical = SyncRemoteLayout.GetBlobPath(spaceId, keyedBlobId);
        return RemoveProtocolRoot(canonical);
    }

    private static string RemoveProtocolRoot(string canonicalPath)
    {
        string prefix = $"{SyncProtocol.ProductDirectoryName}/{SyncProtocol.VersionDirectoryName}/";
        if (!canonicalPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The canonical sync path has an invalid root.");
        }

        return canonicalPath[prefix.Length..];
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
