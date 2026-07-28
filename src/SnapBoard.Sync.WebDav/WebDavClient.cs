using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace SnapBoard.Sync.WebDav;

public sealed class WebDavClient : IDisposable
{
    private static readonly HttpMethod MkColMethod = new("MKCOL");
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private readonly Func<TimeSpan, CancellationToken, ValueTask> _delay;
    private readonly bool _disposeHttpClient;
    private readonly HttpClient _httpClient;
    private readonly WebDavOptions _options;
    private readonly Uri _rootUri;
    private readonly SemaphoreSlim _requestGate;
    private int _disposed;

    public WebDavClient(
        HttpClient httpClient,
        WebDavOptions options,
        bool disposeHttpClient = false)
        : this(
            httpClient,
            options,
            disposeHttpClient,
            static (delay, cancellationToken) =>
                new ValueTask(Task.Delay(delay, cancellationToken)))
    {
    }

    internal WebDavClient(
        HttpClient httpClient,
        WebDavOptions options,
        bool disposeHttpClient,
        Func<TimeSpan, CancellationToken, ValueTask> delay)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _disposeHttpClient = disposeHttpClient;
        _rootUri = WebDavPathPolicy.BuildRootUri(options);
        _requestGate = new SemaphoreSlim(options.MaximumConcurrentRequests);
    }

    public async ValueTask<WebDavResult> EnsureCollectionAsync(
        string relativeCollectionPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!WebDavPathPolicy.IsValidRelativePath(relativeCollectionPath))
        {
            throw new ArgumentException(
                "The WebDAV collection path is invalid.",
                nameof(relativeCollectionPath));
        }

        using RequestLease lease = await AcquireRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        string fullPath = _options.RemoteRoot + "/" + relativeCollectionPath;
        string[] segments = fullPath.Split('/');
        string current = string.Empty;
        foreach (string segment in segments)
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            Uri collectionUri = new(_options.Endpoint, current + "/");
            WebDavSendResult send = await SendWithPolicyAsync(
                    collectionUri,
                    static uri => new HttpRequestMessage(MkColMethod, uri),
                    canRetry: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (send.Failure is not null)
            {
                return send.Failure;
            }

            using HttpResponseMessage response = send.Response!;
            if (response.IsSuccessStatusCode ||
                response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                continue;
            }

            return CreateResult(response);
        }

        return new WebDavResult(true, HttpStatusCode.Created, WebDavErrorCategory.None);
    }

    public async ValueTask<WebDavListResult> ListAsync(
        string relativeCollectionPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using RequestLease lease = await AcquireRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        Uri requestUri = WebDavPathPolicy.BuildRequestUri(
            _rootUri,
            relativeCollectionPath,
            collection: true);
        byte[] requestBody = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:propfind xmlns:d="DAV:">
              <d:prop>
                <d:resourcetype />
                <d:getetag />
                <d:getcontentlength />
              </d:prop>
            </d:propfind>
            """u8.ToArray();
        WebDavSendResult send = await SendWithPolicyAsync(
                requestUri,
                uri =>
                {
                    HttpRequestMessage request = new(PropFindMethod, uri)
                    {
                        Content = new ByteArrayContent(requestBody),
                    };
                    request.Headers.Add("Depth", "1");
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                        "application/xml")
                    {
                        CharSet = "utf-8",
                    };
                    return request;
                },
                canRetry: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (send.Failure is not null)
        {
            return new WebDavListResult(send.Failure, []);
        }

        using HttpResponseMessage response = send.Response!;
        if ((int)response.StatusCode != 207)
        {
            return new WebDavListResult(CreateResult(response), []);
        }

        WebDavBodyReadResult body = await ReadBoundedBodyAsync(
                response,
                _options.MaximumPropFindBytes,
                _options.RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (body.Failure is not null)
        {
            return new WebDavListResult(body.Failure, []);
        }

        try
        {
            IReadOnlyList<WebDavResource> resources = WebDavPropFindParser.Parse(
                body.Content,
                _rootUri,
                requestUri,
                _options.MaximumPropFindBytes,
                _options.MaximumHrefCount);
            return new WebDavListResult(
                CreateResult(response, success: true),
                resources);
        }
        catch (WebDavProtocolException)
        {
            return new WebDavListResult(
                new WebDavResult(
                    false,
                    response.StatusCode,
                    WebDavErrorCategory.Protocol),
                []);
        }
    }

    public async ValueTask<WebDavContentResult> GetAsync(
        string relativeObjectPath,
        int maximumBytes,
        string? ifNoneMatch,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, 90 * 1024 * 1024);
        using RequestLease lease = await AcquireRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        Uri requestUri = WebDavPathPolicy.BuildRequestUri(
            _rootUri,
            relativeObjectPath,
            collection: false);
        WebDavSendResult send = await SendWithPolicyAsync(
                requestUri,
                uri =>
                {
                    HttpRequestMessage request = new(HttpMethod.Get, uri);
                    AddIfNoneMatch(request, ifNoneMatch);
                    return request;
                },
                canRetry: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (send.Failure is not null)
        {
            return new WebDavContentResult(send.Failure);
        }

        using HttpResponseMessage response = send.Response!;
        if (!response.IsSuccessStatusCode)
        {
            return new WebDavContentResult(CreateResult(response));
        }

        WebDavBodyReadResult body = await ReadBoundedBodyAsync(
                response,
                maximumBytes,
                _options.RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return body.Failure is not null
            ? new WebDavContentResult(body.Failure)
            : new WebDavContentResult(CreateResult(response, success: true), body.Content);
    }

    public async ValueTask<WebDavResult> HeadAsync(
        string relativeObjectPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using RequestLease lease = await AcquireRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        Uri requestUri = WebDavPathPolicy.BuildRequestUri(
            _rootUri,
            relativeObjectPath,
            collection: false);
        WebDavSendResult send = await SendWithPolicyAsync(
                requestUri,
                static uri => new HttpRequestMessage(HttpMethod.Head, uri),
                canRetry: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (send.Failure is not null)
        {
            return send.Failure;
        }

        using HttpResponseMessage response = send.Response!;
        return CreateResult(response);
    }

    public async ValueTask<WebDavResult> PutImmutableAsync(
        string relativeObjectPath,
        ReadOnlyMemory<byte> content,
        string mediaType,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (content.IsEmpty || content.Length > 90 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        if (!MediaTypeHeaderValue.TryParse(mediaType, out MediaTypeHeaderValue? parsedMediaType))
        {
            throw new ArgumentException("The WebDAV media type is invalid.", nameof(mediaType));
        }

        using RequestLease lease = await AcquireRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        Uri requestUri = WebDavPathPolicy.BuildRequestUri(
            _rootUri,
            relativeObjectPath,
            collection: false);
        byte[] stableContent = content.ToArray();
        try
        {
            WebDavSendResult send = await SendWithPolicyAsync(
                    requestUri,
                    uri =>
                    {
                        HttpRequestMessage request = new(HttpMethod.Put, uri)
                        {
                            Content = new ByteArrayContent(stableContent),
                        };
                        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                        request.Content.Headers.ContentType = parsedMediaType;
                        return request;
                    },
                    canRetry: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (send.Failure is not null)
            {
                return send.Failure;
            }

            using HttpResponseMessage response = send.Response!;
            return CreateResult(response);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stableContent);
        }
    }

    public async ValueTask<WebDavResult> DeleteAsync(
        string relativeObjectPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using RequestLease lease = await AcquireRequestAsync(cancellationToken)
            .ConfigureAwait(false);
        Uri requestUri = WebDavPathPolicy.BuildRequestUri(
            _rootUri,
            relativeObjectPath,
            collection: false);
        WebDavSendResult send = await SendWithPolicyAsync(
                requestUri,
                static uri => new HttpRequestMessage(HttpMethod.Delete, uri),
                canRetry: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (send.Failure is not null)
        {
            return send.Failure;
        }

        using HttpResponseMessage response = send.Response!;
        return CreateResult(response);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requestGate.Dispose();
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async ValueTask<WebDavSendResult> SendWithPolicyAsync(
        Uri initialUri,
        Func<Uri, HttpRequestMessage> requestFactory,
        bool canRetry,
        CancellationToken cancellationToken)
    {
        Uri redirectRoot = WebDavPathPolicy.IsUnderRoot(_rootUri, initialUri)
            ? _rootUri
            : _options.Endpoint;
        Uri requestUri = initialUri;
        int redirects = 0;
        int retry = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using HttpRequestMessage request = requestFactory(requestUri);
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (canRetry && retry < _options.MaximumRetries)
                {
                    await DelayBeforeRetryAsync(null, retry++, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                return WebDavSendResult.FromFailure(WebDavErrorCategory.Timeout);
            }
            catch (HttpRequestException exception)
            {
                WebDavErrorCategory category = IsCertificateFailure(exception)
                    ? WebDavErrorCategory.Certificate
                    : WebDavErrorCategory.Network;
                if (category == WebDavErrorCategory.Network && canRetry &&
                    retry < _options.MaximumRetries)
                {
                    await DelayBeforeRetryAsync(null, retry++, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                return WebDavSendResult.FromFailure(category);
            }

            if (IsRedirect(response.StatusCode))
            {
                Uri? location = response.Headers.Location;
                if (location is null || redirects >= _options.MaximumRedirects)
                {
                    response.Dispose();
                    return WebDavSendResult.FromFailure(WebDavErrorCategory.Protocol);
                }

                Uri redirected = location.IsAbsoluteUri
                    ? location
                    : new Uri(requestUri, location);
                if (!WebDavPathPolicy.IsSameOrigin(_rootUri, redirected) ||
                    !WebDavPathPolicy.IsUnderRoot(redirectRoot, redirected) ||
                    !CanPreserveMethodAcrossRedirect(request.Method, response.StatusCode))
                {
                    response.Dispose();
                    return WebDavSendResult.FromFailure(WebDavErrorCategory.Protocol);
                }

                response.Dispose();
                requestUri = redirected;
                redirects++;
                continue;
            }

            if (canRetry && IsRetryable(response.StatusCode) &&
                retry < _options.MaximumRetries)
            {
                TimeSpan? retryAfter = GetRetryAfter(response);
                response.Dispose();
                await DelayBeforeRetryAsync(retryAfter, retry++, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            return new WebDavSendResult(response, null);
        }
    }

    private static async ValueTask<WebDavBodyReadResult> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        int maximumBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentEncoding.Any(encoding =>
                !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            return WebDavBodyReadResult.FromFailure(
                response.StatusCode,
                WebDavErrorCategory.Protocol);
        }

        long? declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is < 0 || declaredLength > maximumBytes)
        {
            return WebDavBodyReadResult.FromFailure(
                response.StatusCode,
                WebDavErrorCategory.ResponseTooLarge);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        using CancellationTokenSource bodyTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyTimeout.CancelAfter(timeout);
        CancellationToken bodyCancellation = bodyTimeout.Token;
        try
        {
            int initialCapacity = declaredLength is > 0 and <= int.MaxValue
                ? (int)declaredLength.Value
                : 0;
            using MemoryStream destination = new(initialCapacity);
            await using Stream source = await response.Content
                .ReadAsStreamAsync(bodyCancellation)
                .ConfigureAwait(false);
            while (true)
            {
                int read = await source.ReadAsync(buffer, bodyCancellation).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > maximumBytes)
                {
                    return WebDavBodyReadResult.FromFailure(
                        response.StatusCode,
                        WebDavErrorCategory.ResponseTooLarge);
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), bodyCancellation)
                    .ConfigureAwait(false);
            }

            if (declaredLength.HasValue && destination.Length != declaredLength.Value)
            {
                return WebDavBodyReadResult.FromFailure(
                    response.StatusCode,
                    WebDavErrorCategory.Protocol);
            }

            return new WebDavBodyReadResult(destination.ToArray(), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebDavBodyReadResult.FromFailure(
                response.StatusCode,
                WebDavErrorCategory.Timeout);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            return WebDavBodyReadResult.FromFailure(
                response.StatusCode,
                WebDavErrorCategory.Network);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private async ValueTask DelayBeforeRetryAsync(
        TimeSpan? retryAfter,
        int retry,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = retryAfter ?? TimeSpan.FromMilliseconds(
            Math.Min(30_000, 250 * Math.Pow(2, retry)) * (0.8 + Random.Shared.NextDouble() * 0.4));
        if (delay > MaximumRetryDelay)
        {
            delay = MaximumRetryDelay;
        }

        await _delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static void AddIfNoneMatch(HttpRequestMessage request, string? etag)
    {
        if (etag is null)
        {
            return;
        }

        if (etag.Length > 256 || !request.Headers.TryAddWithoutValidation("If-None-Match", etag))
        {
            throw new ArgumentException("The WebDAV ETag is invalid.", nameof(etag));
        }
    }

    private static bool IsCertificateFailure(HttpRequestException exception) =>
        exception.HttpRequestError == HttpRequestError.SecureConnectionError ||
        exception.InnerException is AuthenticationException;

    private static bool IsRedirect(HttpStatusCode statusCode) => (int)statusCode is
        301 or 302 or 303 or 307 or 308;

    private static bool CanPreserveMethodAcrossRedirect(
        HttpMethod method,
        HttpStatusCode statusCode) =>
        (int)statusCode is 307 or 308 ||
        ((int)statusCode is 301 or 302 &&
         (method == HttpMethod.Get || method == HttpMethod.Head || method == PropFindMethod));

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        (int)statusCode is 408 or 423 or 429 or >= 500 and <= 599;

    private static WebDavResult CreateResult(
        HttpResponseMessage response,
        bool? success = null)
    {
        bool isSuccess = success ?? response.IsSuccessStatusCode;
        return new WebDavResult(
            isSuccess,
            response.StatusCode,
            isSuccess ? WebDavErrorCategory.None : Classify(response.StatusCode),
            NormalizeEtag(response.Headers.ETag?.ToString()),
            GetRetryAfter(response));
    }

    private static WebDavErrorCategory Classify(HttpStatusCode statusCode) =>
        (int)statusCode switch
        {
            401 => WebDavErrorCategory.Authentication,
            403 => WebDavErrorCategory.Permission,
            404 => WebDavErrorCategory.NotFound,
            409 => WebDavErrorCategory.Conflict,
            412 => WebDavErrorCategory.PreconditionFailed,
            423 => WebDavErrorCategory.Locked,
            429 => WebDavErrorCategory.RateLimited,
            >= 500 and <= 599 => WebDavErrorCategory.TransientServer,
            _ => WebDavErrorCategory.Protocol,
        };

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is DateTimeOffset date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        if (delay <= TimeSpan.Zero)
        {
            return null;
        }

        return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
    }

    private static string? NormalizeEtag(string? etag) =>
        string.IsNullOrWhiteSpace(etag) || etag.Length > 256 ? null : etag;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private async ValueTask<RequestLease> AcquireRequestAsync(
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RequestLease(_requestGate);
    }

    private sealed class RequestLease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public RequestLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            SemaphoreSlim? gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }

    private sealed record WebDavSendResult(
        HttpResponseMessage? Response,
        WebDavResult? Failure)
    {
        public static WebDavSendResult FromFailure(WebDavErrorCategory category) => new(
            null,
            new WebDavResult(false, null, category));
    }

    private sealed record WebDavBodyReadResult(
        byte[] Content,
        WebDavResult? Failure)
    {
        public static WebDavBodyReadResult FromFailure(
            HttpStatusCode statusCode,
            WebDavErrorCategory category) => new(
                [],
                new WebDavResult(false, statusCode, category));
    }
}
