using System.Security.Cryptography;

namespace SnapBoard.Update.Velopack.Tests;

public sealed class UpdateEndpointOptionsTests
{
    [Fact]
    public void ParseHttpsBaseUriNormalizesTrailingSlash()
    {
        Uri uri = UpdateEndpointOptions.ParseHttpsBaseUri("https://updates.example.test/feed");

        Assert.Equal("https://updates.example.test/feed/", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://updates.example.test/")]
    [InlineData("https://user:secret@updates.example.test/")]
    [InlineData("https://updates.example.test/?token=secret")]
    [InlineData("https://updates.example.test/#fragment")]
    public void ParseHttpsBaseUriRejectsUnsafeEndpoint(string value) =>
        Assert.Throws<InvalidOperationException>(() => UpdateEndpointOptions.ParseHttpsBaseUri(value));

    [Fact]
    public void ProductionPublicKeyIsP256SubjectPublicKeyInfo()
    {
        byte[] encoded = Convert.FromBase64String(
            UpdateEndpointOptions.ProductionPublicKeySubjectPublicKeyInfoBase64);
        using ECDsa verifier = ECDsa.Create();

        verifier.ImportSubjectPublicKeyInfo(encoded, out int bytesRead);

        Assert.Equal(encoded.Length, bytesRead);
        Assert.Equal(256, verifier.KeySize);
    }

    [Fact]
    public void ProductionPublicKeyMatchesReleaseSigningPublicKey()
    {
        string publicKeyPath = Path.Combine(
            AppContext.BaseDirectory,
            "packaging",
            "updates",
            "update-signing-public.pem");
        using ECDsa verifier = ECDsa.Create();

        verifier.ImportFromPem(File.ReadAllText(publicKeyPath));

        Assert.Equal(
            UpdateEndpointOptions.ProductionPublicKeySubjectPublicKeyInfoBase64,
            Convert.ToBase64String(verifier.ExportSubjectPublicKeyInfo()));
    }
}
