namespace SnapBoard.Sync.WebDav;

/// <summary>
/// WebDAV 连接的非敏感配置。用户名、应用密码和端到端加密密钥
/// 必须存入操作系统凭据存储，不能放进此对象的持久化配置中。
/// </summary>
public sealed class WebDavOptions
{
    public WebDavOptions(Uri endpoint, string remoteRoot = "SnapBoard/v1")
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("WebDAV endpoint must use an absolute HTTPS URI.", nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(remoteRoot))
        {
            throw new ArgumentException("Remote root cannot be empty.", nameof(remoteRoot));
        }

        Endpoint = endpoint;
        RemoteRoot = remoteRoot.Trim('/');
    }

    public Uri Endpoint { get; }

    public string RemoteRoot { get; }
}
