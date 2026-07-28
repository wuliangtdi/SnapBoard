using System.Net;
using System.Text;
using SnapBoard.Application.Sync;

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
