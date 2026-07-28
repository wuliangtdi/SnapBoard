using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SnapBoard.Sync.WebDav.Tests;

public sealed class WebDavHttpClientFactoryTests
{
    [Fact]
    public void ExactPinAllowsOnlyChainErrorsForSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=dav.example.test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        byte[] exactPin = certificate.GetCertHash(HashAlgorithmName.SHA256);
        byte[] wrongPin = exactPin.ToArray();
        wrongPin[0] ^= 0xff;
        try
        {
            Assert.True(WebDavHttpClientFactory.ValidatePinnedCertificate(
                exactPin,
                certificate,
                SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(WebDavHttpClientFactory.ValidatePinnedCertificate(
                wrongPin,
                certificate,
                SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(WebDavHttpClientFactory.ValidatePinnedCertificate(
                exactPin,
                certificate,
                SslPolicyErrors.RemoteCertificateNameMismatch |
                SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(WebDavHttpClientFactory.ValidatePinnedCertificate(
                exactPin,
                certificate: null,
                SslPolicyErrors.RemoteCertificateNotAvailable));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exactPin);
            CryptographicOperations.ZeroMemory(wrongPin);
        }
    }
}
