namespace SnapBoard.Update.Velopack;

public sealed record UpdateEndpointOptions(
    Uri GitHubRepository,
    Uri? OfficialBaseUri,
    string PublicKeySubjectPublicKeyInfoBase64)
{
    public const string OfficialBaseUrlEnvironmentVariable =
        "SNAPBOARD_UPDATE_OFFICIAL_BASE_URL";

    // 发布私钥只保存在维护者的密码库和 CI Secret；客户端仅携带此 P-256 公钥。
    public const string ProductionPublicKeySubjectPublicKeyInfoBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEdajcKAJ3BG6vpkLDNpw2WEREfraa" +
        "utd7GvwRJMl70jxNeBUTtT4+cctPsELXUiusEknhJ9h0eN5GbjK3D7RxaQ==";

    public static UpdateEndpointOptions CreateDefault()
    {
        Uri? officialBaseUri = null;
        string? configuredOfficialBaseUrl = Environment.GetEnvironmentVariable(
            OfficialBaseUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredOfficialBaseUrl))
        {
            officialBaseUri = ParseHttpsBaseUri(configuredOfficialBaseUrl);
        }

        return new UpdateEndpointOptions(
            new Uri("https://github.com/wuliangtdi/SnapBoard", UriKind.Absolute),
            officialBaseUri,
            ProductionPublicKeySubjectPublicKeyInfoBase64);
    }

    internal static Uri ParseHttpsBaseUri(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 2048 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("The official update source URL is invalid.");
        }

        string absolute = uri.AbsoluteUri.EndsWith('/')
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
        return new Uri(absolute, UriKind.Absolute);
    }
}
