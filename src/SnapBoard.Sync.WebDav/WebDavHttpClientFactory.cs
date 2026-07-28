using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SnapBoard.Sync.WebDav;

public static class WebDavHttpClientFactory
{
    public static HttpClient Create(
        WebDavOptions options,
        ICredentials? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            Credentials = credentials,
            MaxConnectionsPerServer = options.MaximumConcurrentRequests,
            MaxResponseHeadersLength = 64,
            PreAuthenticate = false,
            UseCookies = false,
        };
        if (options.CertificateSha256Pin is not null)
        {
            byte[] expectedPin = Convert.FromHexString(options.CertificateSha256Pin);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                ValidatePinnedCertificate(expectedPin, certificate, errors);
        }

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    internal static bool ValidatePinnedCertificate(
        ReadOnlySpan<byte> expectedPin,
        X509Certificate2? certificate,
        SslPolicyErrors errors)
    {
        if (expectedPin.Length != SHA256.HashSizeInBytes || certificate is null ||
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        byte[] actualPin = certificate.GetCertHash(HashAlgorithmName.SHA256);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedPin, actualPin);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualPin);
        }
    }
}
