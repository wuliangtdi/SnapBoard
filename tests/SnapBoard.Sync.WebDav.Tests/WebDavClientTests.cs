using System.Net;
using System.Text;

namespace SnapBoard.Sync.WebDav.Tests;

public sealed class WebDavClientTests
{
    private const string CollectionPath =
        "spaces/11111111111111111111111111111111/devices/22222222222222222222222222222222/events";
    private const string EventName =
        "00000000000000000042-33333333333333333333333333333333.enc";

    [Fact]
    public async Task EnsuresHierarchyAndUploadsImmutableObjectWithStableRetry()
    {
        List<RecordedRequest> requests = [];
        int putCount = 0;
        RecordingHandler handler = new(async (request, cancellationToken) =>
        {
            requests.Add(await RecordedRequest.CreateAsync(request, cancellationToken));
            if (request.Method.Method == "PUT")
            {
                putCount++;
                return new HttpResponseMessage(
                    putCount == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using HttpClient httpClient = new(handler);
        WebDavOptions options = CreateOptions(maximumRetries: 1);
        using WebDavClient client = new(
            httpClient,
            options,
            disposeHttpClient: false,
            static (_, _) => ValueTask.CompletedTask);

        WebDavResult collection = await client.EnsureCollectionAsync(
            CollectionPath,
            CancellationToken.None);
        byte[] body = "encrypted-object"u8.ToArray();
        WebDavResult put = await client.PutImmutableAsync(
            CollectionPath + "/" + EventName,
            body,
            "application/octet-stream",
            CancellationToken.None);

        Assert.True(collection.IsSuccess);
        Assert.True(put.IsSuccess);
        RecordedRequest[] puts = requests.Where(request => request.Method == "PUT").ToArray();
        Assert.Equal(2, puts.Length);
        Assert.All(puts, request => Assert.Equal("*", request.IfNoneMatch));
        Assert.All(puts, request => Assert.Equal(body, request.Body));
        Assert.Equal(2, putCount);
        Assert.Equal("MKCOL", requests[0].Method);
        Assert.Equal("https://dav.example.test/base/SnapBoard/", requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task PropFindParsesBoundedDavResources()
    {
        string collectionHref =
            "/base/SnapBoard/v1/" + CollectionPath + "/";
        string eventHref = collectionHref + EventName;
        string xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>{{collectionHref}}</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>{{eventHref}}</d:href>
                <d:propstat><d:prop><d:resourcetype /><d:getetag>"etag-1"</d:getetag><d:getcontentlength>321</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        using WebDavClient client = CreateClient(_ => XmlResponse(xml));

        WebDavListResult result = await client.ListAsync(
            CollectionPath,
            CancellationToken.None);

        Assert.True(result.Result.IsSuccess);
        Assert.Equal(2, result.Resources.Count);
        WebDavResource resource = Assert.Single(result.Resources, item => !item.IsCollection);
        Assert.Equal(EventName, resource.ObjectName);
        Assert.Equal("\"etag-1\"", resource.ETag);
        Assert.Equal(321, resource.ContentLength);
    }

    [Fact]
    public async Task PropFindTreatsOverlongEtagAsUnavailable()
    {
        string collectionHref =
            "/base/SnapBoard/v1/" + CollectionPath + "/";
        string eventHref = collectionHref + EventName;
        string overlongEtag = new('e', 257);
        string xml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>{{eventHref}}</d:href>
                <d:propstat><d:prop><d:resourcetype /><d:getetag>{{overlongEtag}}</d:getetag><d:getcontentlength>321</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        using WebDavClient client = CreateClient(_ => XmlResponse(xml));

        WebDavListResult result = await client.ListAsync(
            CollectionPath,
            CancellationToken.None);

        Assert.True(result.Result.IsSuccess);
        WebDavResource resource = Assert.Single(result.Resources);
        Assert.Equal(EventName, resource.ObjectName);
        Assert.Null(resource.ETag);
        Assert.Equal(321, resource.ContentLength);
    }

    [Theory]
    [MemberData(nameof(UnsafePropFindResponses))]
    public async Task PropFindRejectsMaliciousXmlAndHref(string xml)
    {
        using WebDavClient client = CreateClient(_ => XmlResponse(xml));

        WebDavListResult result = await client.ListAsync(
            CollectionPath,
            CancellationToken.None);

        Assert.False(result.Result.IsSuccess);
        Assert.Equal(WebDavErrorCategory.Protocol, result.Result.ErrorCategory);
        Assert.Empty(result.Resources);
    }

    [Fact]
    public async Task CrossOriginRedirectIsRejectedWithoutFollowing()
    {
        int requests = 0;
        using WebDavClient client = CreateClient(_ =>
        {
            requests++;
            HttpResponseMessage response = new(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://attacker.example/steal");
            return response;
        });

        WebDavResult result = await client.HeadAsync(
            CollectionPath + "/" + EventName,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WebDavErrorCategory.Protocol, result.ErrorCategory);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(401, WebDavErrorCategory.Authentication)]
    [InlineData(403, WebDavErrorCategory.Permission)]
    [InlineData(404, WebDavErrorCategory.NotFound)]
    [InlineData(409, WebDavErrorCategory.Conflict)]
    [InlineData(412, WebDavErrorCategory.PreconditionFailed)]
    [InlineData(423, WebDavErrorCategory.Locked)]
    [InlineData(429, WebDavErrorCategory.RateLimited)]
    [InlineData(503, WebDavErrorCategory.TransientServer)]
    [InlineData(507, WebDavErrorCategory.TransientServer)]
    public async Task ClassifiesProtocolStatusCodes(
        int statusCode,
        WebDavErrorCategory expected)
    {
        using WebDavClient client = CreateClient(
            _ => new HttpResponseMessage((HttpStatusCode)statusCode),
            maximumRetries: 0);

        WebDavResult result = await client.HeadAsync(
            CollectionPath + "/" + EventName,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.ErrorCategory);
    }

    [Fact]
    public async Task GetRejectsOversizedAndCompressedBodies()
    {
        int request = 0;
        using WebDavClient client = CreateClient(_ =>
        {
            request++;
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[101]),
            };
            if (request == 2)
            {
                response.Content.Headers.ContentEncoding.Add("gzip");
            }

            return response;
        });

        WebDavContentResult oversized = await client.GetAsync(
            CollectionPath + "/" + EventName,
            100,
            ifNoneMatch: null,
            CancellationToken.None);
        WebDavContentResult compressed = await client.GetAsync(
            CollectionPath + "/" + EventName,
            200,
            ifNoneMatch: null,
            CancellationToken.None);

        Assert.Equal(WebDavErrorCategory.ResponseTooLarge, oversized.Result.ErrorCategory);
        Assert.Equal(WebDavErrorCategory.Protocol, compressed.Result.ErrorCategory);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        RecordingHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using HttpClient httpClient = new(handler);
        using WebDavClient client = new(httpClient, CreateOptions());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.HeadAsync(
                CollectionPath + "/" + EventName,
                cancellation.Token));
    }

    public static TheoryData<string> UnsafePropFindResponses => new()
    {
        """
        <?xml version="1.0"?>
        <!DOCTYPE multistatus [<!ENTITY xxe SYSTEM "file:///windows/win.ini">]>
        <multistatus xmlns="DAV:"><response><href>&xxe;</href></response></multistatus>
        """,
        """
        <multistatus xmlns="DAV:"><response><href>https://attacker.example/object.enc</href></response></multistatus>
        """,
        """
        <multistatus xmlns="DAV:"><response><href>/base/SnapBoard/v1/spaces/%2e%2e/secret.enc</href></response></multistatus>
        """,
        """
        <multistatus xmlns="DAV:"><response><href>/base/SnapBoard/v1/spaces%2fsecret.enc</href></response></multistatus>
        """,
        """
        <multistatus xmlns="DAV:"><response><href>/outside/root.enc</href></response></multistatus>
        """,
    };

    private static WebDavClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        int maximumRetries = 0)
    {
        RecordingHandler handler = new((request, _) =>
            Task.FromResult(responseFactory(request)));
        HttpClient httpClient = new(handler);
        return new WebDavClient(
            httpClient,
            CreateOptions(maximumRetries),
            disposeHttpClient: true,
            static (_, _) => ValueTask.CompletedTask);
    }

    private static WebDavOptions CreateOptions(int maximumRetries = 0) => new(
        new Uri("https://dav.example.test/base/"),
        maximumRetries: maximumRetries,
        requestTimeout: TimeSpan.FromSeconds(5));

    private static HttpResponseMessage XmlResponse(string xml) => new(
        (HttpStatusCode)207)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            _handler;

        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }

    private sealed record RecordedRequest(
        string Method,
        Uri Uri,
        string? IfNoneMatch,
        byte[] Body)
    {
        public static async Task<RecordedRequest> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new RecordedRequest(
                request.Method.Method,
                request.RequestUri!,
                request.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? values)
                    ? Assert.Single(values)
                    : null,
                body);
        }
    }
}
