using System.Net;

namespace SnapBoard.Sync.WebDav;

public enum WebDavErrorCategory
{
    None = 0,
    Authentication = 1,
    Permission = 2,
    NotFound = 3,
    Conflict = 4,
    PreconditionFailed = 5,
    Locked = 6,
    RateLimited = 7,
    TransientServer = 8,
    Network = 9,
    Timeout = 10,
    Certificate = 11,
    Protocol = 12,
    ResponseTooLarge = 13,
}

public sealed record WebDavResult(
    bool IsSuccess,
    HttpStatusCode? StatusCode,
    WebDavErrorCategory ErrorCategory,
    string? ETag = null,
    TimeSpan? RetryAfter = null);

public sealed record WebDavContentResult(
    WebDavResult Result,
    byte[]? Content = null);

public sealed record WebDavResource(
    string RelativePath,
    string ObjectName,
    bool IsCollection,
    string? ETag,
    long? ContentLength);

public sealed record WebDavListResult(
    WebDavResult Result,
    IReadOnlyList<WebDavResource> Resources);

public sealed class WebDavProtocolException : Exception
{
    public WebDavProtocolException(string message)
        : base(message)
    {
    }

    public WebDavProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
