using System.Security.Cryptography;
using System.Text;
using Velopack.Sources;

namespace SnapBoard.Update.Velopack.Tests;

public sealed class SignedFileDownloaderTests
{
    private const string FeedUrl = "https://updates.example.test/releases.osx-stable.json";

    [Fact]
    public async Task DownloadStringAcceptsFeedSignedByEmbeddedPublicKey()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] feed = Encoding.UTF8.GetBytes("{\"Assets\":[]}");
        byte[] signature = signer.SignData(
            feed,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        MemoryDownloader inner = new(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [FeedUrl] = feed,
            [$"{FeedUrl}.sig"] = signature,
        });
        SignedFileDownloader downloader = new(
            inner,
            Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));

        string actual = await downloader.DownloadString(FeedUrl);

        Assert.Equal("{\"Assets\":[]}", actual);
        Assert.Equal([FeedUrl, $"{FeedUrl}.sig"], inner.Requests);
    }

    [Fact]
    public async Task DownloadBytesRejectsTamperedFeed()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] original = Encoding.UTF8.GetBytes("{\"Assets\":[]}");
        byte[] tampered = Encoding.UTF8.GetBytes("{\"Assets\":[{}]}");
        byte[] signature = signer.SignData(
            original,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        MemoryDownloader inner = new(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [FeedUrl] = tampered,
            [$"{FeedUrl}.sig"] = signature,
        });
        SignedFileDownloader downloader = new(
            inner,
            Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));

        await Assert.ThrowsAsync<UpdateSignatureException>(
            () => downloader.DownloadBytes(FeedUrl));
    }

    [Theory]
    [InlineData("https://updates.example.test/releases.osx-stable.json", true)]
    [InlineData("https://updates.example.test/releases.OSX-stable.json", false)]
    [InlineData("https://updates.example.test/releases..json", false)]
    [InlineData("https://updates.example.test/package.nupkg", false)]
    public void ReleaseFeedDetectionIsStrict(string url, bool expected) =>
        Assert.Equal(expected, SignedFileDownloader.IsReleaseFeedUrl(url));

    private sealed class MemoryDownloader(
        IReadOnlyDictionary<string, byte[]> responses) : IFileDownloader
    {
        public List<string> Requests { get; } = [];

        public Task DownloadFile(
            string url,
            string targetFile,
            Action<int> progress,
            IDictionary<string, string>? headers = null,
            double timeout = 30,
            CancellationToken cancelToken = default) => throw new NotSupportedException();

        public Task<byte[]> DownloadBytes(
            string url,
            IDictionary<string, string>? headers = null,
            double timeout = 30)
        {
            Requests.Add(url);
            return Task.FromResult(responses[url].ToArray());
        }

        public async Task<string> DownloadString(
            string url,
            IDictionary<string, string>? headers = null,
            double timeout = 30) => Encoding.UTF8.GetString(
                await DownloadBytes(url, headers, timeout));
    }
}
