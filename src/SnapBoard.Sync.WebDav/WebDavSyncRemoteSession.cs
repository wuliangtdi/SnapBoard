using System.Net;
using System.Text;
using SnapBoard.Application.Sync;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Sync.WebDav;

public sealed class WebDavSyncRemoteSessionFactory : ISyncRemoteSessionFactory
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public ISyncRemoteSession Create(
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

public sealed class WebDavSyncRemoteSession : ISyncRemoteSession
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
