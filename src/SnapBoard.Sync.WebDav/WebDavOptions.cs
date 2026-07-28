using SnapBoard.Sync.Contracts;

namespace SnapBoard.Sync.WebDav;

/// <summary>WebDAV 非敏感配置；用户名、密码和内容密钥不属于此对象。</summary>
public sealed class WebDavOptions
{
    public WebDavOptions(
        Uri endpoint,
        string remoteRoot = "SnapBoard/v1",
        bool allowInsecureLoopback = false,
        string? certificateSha256Pin = null,
        int maximumConcurrentRequests = 4,
        int maximumRetries = 3,
        int maximumRedirects = 3,
        int maximumPropFindBytes = 2 * 1024 * 1024,
        int maximumHrefCount = 10_000,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "WebDAV endpoint must be absolute and cannot contain credentials, query, or fragment.",
                nameof(endpoint));
        }

        bool secure = endpoint.Scheme == Uri.UriSchemeHttps;
        bool explicitLoopbackDevelopment =
            allowInsecureLoopback && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback;
        if (!secure && !explicitLoopbackDevelopment)
        {
            throw new ArgumentException(
                "WebDAV endpoint must use HTTPS; HTTP is limited to explicit loopback development.",
                nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(remoteRoot) ||
            !WebDavPathPolicy.IsValidRelativePath(remoteRoot.Trim('/')))
        {
            throw new ArgumentException("Remote root is invalid.", nameof(remoteRoot));
        }

        if (certificateSha256Pin is not null &&
            !SyncRemoteLayout.IsLowerHex(certificateSha256Pin, 64))
        {
            throw new ArgumentException(
                "Certificate pin must be lowercase SHA-256 hex.",
                nameof(certificateSha256Pin));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentRequests, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentRequests, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetries);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRetries, 5);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRedirects);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRedirects, 5);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPropFindBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPropFindBytes, 8 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumHrefCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumHrefCount, 10_000);
        TimeSpan timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        Endpoint = EnsureTrailingSlash(endpoint);
        RemoteRoot = remoteRoot.Trim('/');
        AllowInsecureLoopback = explicitLoopbackDevelopment;
        CertificateSha256Pin = certificateSha256Pin;
        MaximumConcurrentRequests = maximumConcurrentRequests;
        MaximumRetries = maximumRetries;
        MaximumRedirects = maximumRedirects;
        MaximumPropFindBytes = maximumPropFindBytes;
        MaximumHrefCount = maximumHrefCount;
        RequestTimeout = timeout;
    }

    public Uri Endpoint { get; }

    public string RemoteRoot { get; }

    public bool AllowInsecureLoopback { get; }

    public string? CertificateSha256Pin { get; }

    public int MaximumConcurrentRequests { get; }

    public int MaximumRetries { get; }

    public int MaximumRedirects { get; }

    public int MaximumPropFindBytes { get; }

    public int MaximumHrefCount { get; }

    public TimeSpan RequestTimeout { get; }

    private static Uri EnsureTrailingSlash(Uri endpoint)
    {
        UriBuilder builder = new(endpoint);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }
}
