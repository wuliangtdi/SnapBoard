using System.Net;
using System.Text;
using SnapBoard.Application.Sync;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Sync.WebDav.Tests;

public sealed class WebDavSyncRemoteSessionTests
{
    private static readonly Guid SpaceId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeviceId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FirstEventId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecondEventId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid MigrationPlanId =
        Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid SecondMigrationPlanId =
        Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public async Task EnsureHierarchyCreatesCanonicalCollections()
    {
        List<string> collectionUris = [];
        await using WebDavSyncRemoteSession session = CreateSession(request =>
        {
            Assert.Equal("MKCOL", request.Method.Method);
            collectionUris.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        SyncRemoteResult result = await session.EnsureHierarchyAsync(
            SpaceId,
            DeviceId,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        const string root =
            "https://dav.example.test/base/SnapBoard/v1/spaces/11111111111111111111111111111111";
        Assert.Contains(
            $"{root}/devices/22222222222222222222222222222222/events/",
            collectionUris);
        Assert.Contains(
            $"{root}/devices/22222222222222222222222222222222/checkpoints/",
            collectionUris);
        Assert.Contains($"{root}/blobs/", collectionUris);
    }

    [Fact]
    public async Task ImmutableEventPreconditionFailureIsSuccessfulDuplicate()
    {
        string? requestUri = null;
        string? ifNoneMatch = null;
        string? mediaType = null;
        byte[]? requestBody = null;
        await using WebDavSyncRemoteSession session = CreateSession(async (
            request,
            cancellationToken) =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            ifNoneMatch = Assert.Single(request.Headers.IfNoneMatch).ToString();
            mediaType = request.Content!.Headers.ContentType!.MediaType;
            requestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        });
        byte[] encryptedEvent = "encrypted-event"u8.ToArray();

        SyncRemoteResult result = await session.PutEventAsync(
            SpaceId,
            DeviceId,
            42,
            FirstEventId,
            encryptedEvent,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.AlreadyExisted);
        Assert.Equal(
            "https://dav.example.test/base/SnapBoard/v1/spaces/11111111111111111111111111111111/" +
            "devices/22222222222222222222222222222222/events/" +
            "00000000000000000042-33333333333333333333333333333333.enc",
            requestUri);
        Assert.Equal("*", ifNoneMatch);
        Assert.Equal("application/vnd.snapboard.encrypted+json", mediaType);
        Assert.Equal(encryptedEvent, requestBody);
    }

    [Fact]
    public async Task ListEventsReturnsCanonicalSequenceOrder()
    {
        string collection =
            "/base/SnapBoard/v1/spaces/11111111111111111111111111111111/" +
            "devices/22222222222222222222222222222222/events/";
        string xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>{{collection}}</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>{{collection}}00000000000000000009-44444444444444444444444444444444.enc</d:href>
                <d:propstat><d:prop><d:resourcetype /><d:getetag>"etag-9"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>{{collection}}00000000000000000003-33333333333333333333333333333333.enc</d:href>
                <d:propstat><d:prop><d:resourcetype /><d:getetag>"etag-3"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        await using WebDavSyncRemoteSession session = CreateSession(
            _ => XmlResponse(xml));

        SyncRemoteEventListResult result = await session.ListEventsAsync(
            SpaceId,
            DeviceId,
            CancellationToken.None);

        Assert.True(result.Result.IsSuccess);
        Assert.Collection(
            result.Events,
            item =>
            {
                Assert.Equal(3, item.Sequence);
                Assert.Equal(FirstEventId, item.EventId);
                Assert.Equal("\"etag-3\"", item.ETag);
            },
            item =>
            {
                Assert.Equal(9, item.Sequence);
                Assert.Equal(SecondEventId, item.EventId);
                Assert.Equal("\"etag-9\"", item.ETag);
            });
    }

    [Fact]
    public async Task EnsureMigrationHierarchyCreatesCanonicalPlanCollections()
    {
        List<string> collectionUris = [];
        await using WebDavSyncRemoteSession session = CreateSession(request =>
        {
            Assert.Equal("MKCOL", request.Method.Method);
            collectionUris.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        SyncRemoteResult result = await session.EnsureMigrationHierarchyAsync(
            SpaceId,
            MigrationPlanId,
            [DeviceId],
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        const string root =
            "https://dav.example.test/base/SnapBoard/v1/spaces/11111111111111111111111111111111/" +
            "migrations/55555555555555555555555555555555";
        Assert.Contains("https://dav.example.test/base/SnapBoard/v1/spaces/" +
            "11111111111111111111111111111111/migrations/", collectionUris);
        Assert.Contains($"{root}/", collectionUris);
        Assert.Contains($"{root}/ready/", collectionUris);
        Assert.Contains($"{root}/committed/", collectionUris);
        Assert.Contains($"{root}/rolled-back/", collectionUris);
    }

    [Fact]
    public async Task MigrationMarkerUsesCanonicalImmutablePath()
    {
        string? requestUri = null;
        string? ifNoneMatch = null;
        byte[]? requestBody = null;
        await using WebDavSyncRemoteSession session = CreateSession(async (
            request,
            cancellationToken) =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            ifNoneMatch = Assert.Single(request.Headers.IfNoneMatch).ToString();
            requestBody = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        });
        byte[] encryptedMarker = "encrypted-terminal-marker"u8.ToArray();

        SyncRemoteResult result = await session.PutProviderMigrationMarkerAsync(
            SpaceId,
            new SyncProviderMigrationMarkerAddress(
                MigrationPlanId,
                SyncProviderMigrationMarkerKind.Terminal),
            encryptedMarker,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.AlreadyExisted);
        Assert.Equal("*", ifNoneMatch);
        Assert.Equal(encryptedMarker, requestBody);
        Assert.Equal(
            "https://dav.example.test/base/SnapBoard/v1/spaces/" +
            "11111111111111111111111111111111/migrations/" +
            "55555555555555555555555555555555/terminal.enc",
            requestUri);
    }

    [Fact]
    public async Task ProviderMigrationQuotaFailureMapsToTransientError()
    {
        await using WebDavSyncRemoteSession session = CreateSession(
            _ => new HttpResponseMessage((HttpStatusCode)507));

        SyncRemoteResult result = await session.PutProviderMigrationMarkerAsync(
            SpaceId,
            new SyncProviderMigrationMarkerAddress(
                MigrationPlanId,
                SyncProviderMigrationMarkerKind.Intent),
            "encrypted-intent"u8.ToArray(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(SyncRemoteErrorCategory.Transient, result.ErrorCategory);
    }

    [Fact]
    public async Task MigrationPlansUseCanonicalCollectionsAndStableOrder()
    {
        const string collection =
            "/base/SnapBoard/v1/spaces/11111111111111111111111111111111/migrations";
        await using WebDavSyncRemoteSession session = CreateSession(request =>
        {
            Assert.Equal("PROPFIND", request.Method.Method);
            Assert.Equal($"{collection}/", request.RequestUri!.AbsolutePath);
            return XmlResponse(CollectionXml(
                $"{collection}/",
                ($"{collection}/{SecondMigrationPlanId:N}/", true, "\"second\"", null),
                ($"{collection}/{MigrationPlanId:N}/", true, "\"first\"", null)));
        });

        SyncRemoteProviderMigrationPlanListResult result = await session
            .ListProviderMigrationPlansAsync(SpaceId, CancellationToken.None);

        Assert.True(result.Result.IsSuccess);
        Assert.Collection(
            result.Plans,
            plan =>
            {
                Assert.Equal(MigrationPlanId, plan.PlanId);
                Assert.Equal("\"first\"", plan.ETag);
            },
            plan =>
            {
                Assert.Equal(SecondMigrationPlanId, plan.PlanId);
                Assert.Equal("\"second\"", plan.ETag);
            });
    }

    [Fact]
    public async Task CiphertextInventoryReturnsCanonicalObjectOrder()
    {
        const string spaceRoot =
            "/base/SnapBoard/v1/spaces/11111111111111111111111111111111";
        string deviceRoot = $"{spaceRoot}/devices/22222222222222222222222222222222";
        string blobId = new('a', 64);
        await using WebDavSyncRemoteSession session = CreateSession(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"{spaceRoot}/metadata.enc")
            {
                return BinaryResponse("encrypted-metadata"u8.ToArray(), "\"metadata-etag\"");
            }

            Assert.Equal("PROPFIND", request.Method.Method);
            if (path == $"{spaceRoot}/devices/")
            {
                return XmlResponse(CollectionXml(
                    $"{spaceRoot}/devices/",
                    ($"{deviceRoot}/", true, null, null)));
            }

            if (path == $"{deviceRoot}/events/")
            {
                return XmlResponse(CollectionXml(
                    $"{deviceRoot}/events/",
                    ($"{deviceRoot}/events/00000000000000000003-" +
                        "33333333333333333333333333333333.enc", false, "\"event-etag\"", 21)));
            }

            if (path == $"{deviceRoot}/checkpoints/")
            {
                return XmlResponse(CollectionXml($"{deviceRoot}/checkpoints/"));
            }

            if (path == $"{spaceRoot}/blobs/")
            {
                return XmlResponse(CollectionXml(
                    $"{spaceRoot}/blobs/",
                    ($"{spaceRoot}/blobs/{blobId}.enc", false, "\"blob-etag\"", 34)));
            }

            throw new InvalidOperationException($"Unexpected request path: {path}");
        });

        SyncRemoteCiphertextObjectListResult result = await session
            .ListCiphertextObjectsAsync(SpaceId, CancellationToken.None);

        Assert.True(result.Result.IsSuccess);
        Assert.Collection(
            result.Objects,
            item =>
            {
                Assert.Equal(SyncObjectType.Metadata, item.ObjectType);
                Assert.Equal(18, item.ContentLength);
            },
            item =>
            {
                Assert.Equal(SyncObjectType.Event, item.ObjectType);
                Assert.Equal(3, item.Sequence);
                Assert.Equal(FirstEventId, item.EventId);
            },
            item =>
            {
                Assert.Equal(SyncObjectType.Blob, item.ObjectType);
                Assert.Equal(blobId, item.KeyedBlobId);
                Assert.Equal(34, item.ContentLength);
            });
    }

    [Fact]
    public async Task CiphertextInventoryRejectsUnexpectedCheckpointObjects()
    {
        const string spaceRoot =
            "/base/SnapBoard/v1/spaces/11111111111111111111111111111111";
        string deviceRoot = $"{spaceRoot}/devices/22222222222222222222222222222222";
        await using WebDavSyncRemoteSession session = CreateSession(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"{spaceRoot}/metadata.enc")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            Assert.Equal("PROPFIND", request.Method.Method);
            if (path == $"{spaceRoot}/devices/")
            {
                return XmlResponse(CollectionXml(
                    $"{spaceRoot}/devices/",
                    ($"{deviceRoot}/", true, null, null)));
            }

            if (path == $"{deviceRoot}/events/")
            {
                return XmlResponse(CollectionXml($"{deviceRoot}/events/"));
            }

            if (path == $"{deviceRoot}/checkpoints/")
            {
                return XmlResponse(CollectionXml(
                    $"{deviceRoot}/checkpoints/",
                    ($"{deviceRoot}/checkpoints/{DeviceId:N}.enc", false, "\"etag\"", 10)));
            }

            throw new InvalidOperationException($"Unexpected request path: {path}");
        });

        SyncRemoteCiphertextObjectListResult result = await session
            .ListCiphertextObjectsAsync(SpaceId, CancellationToken.None);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal(SyncRemoteErrorCategory.Protocol, result.Result.ErrorCategory);
        Assert.Empty(result.Objects);
    }

    private static WebDavSyncRemoteSession CreateSession(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        CreateSession((request, _) => Task.FromResult(responseFactory(request)));

    private static WebDavSyncRemoteSession CreateSession(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        HttpClient httpClient = new(new DelegateHandler(handler));
        WebDavOptions options = new(
            new Uri("https://dav.example.test/base/"),
            maximumRetries: 0,
            requestTimeout: TimeSpan.FromSeconds(5));
        WebDavClient client = new(
            httpClient,
            options,
            disposeHttpClient: true,
            static (_, _) => ValueTask.CompletedTask);
        return new WebDavSyncRemoteSession(client);
    }

    private static HttpResponseMessage XmlResponse(string xml) => new(
        (HttpStatusCode)207)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
    };

    private static HttpResponseMessage BinaryResponse(byte[] content, string etag)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private static string CollectionXml(
        string collection,
        params (string Path, bool IsCollection, string? ETag, long? Length)[] resources)
    {
        StringBuilder builder = new();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .Append("<d:multistatus xmlns:d=\"DAV:\">")
            .Append(ResourceXml(collection, isCollection: true, etag: null, length: null));
        foreach ((string path, bool isCollection, string? etag, long? length) in resources)
        {
            builder.Append(ResourceXml(path, isCollection, etag, length));
        }

        return builder.Append("</d:multistatus>").ToString();
    }

    private static string ResourceXml(
        string path,
        bool isCollection,
        string? etag,
        long? length) =>
        $"<d:response><d:href>{path}</d:href><d:propstat><d:prop>" +
        (isCollection
            ? "<d:resourcetype><d:collection /></d:resourcetype>"
            : "<d:resourcetype />") +
        (etag is null ? string.Empty : $"<d:getetag>{etag}</d:getetag>") +
        (length is null ? string.Empty : $"<d:getcontentlength>{length}</d:getcontentlength>") +
        "</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _handler;

        public DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }
}
