using System.Security.Cryptography;
using System.Text;
using Velopack.Sources;

namespace SnapBoard.Update.Velopack;

internal sealed class SignedFileDownloader : IFileDownloader
{
    private const int MaximumFeedBytes = 2 * 1024 * 1024;
    private const int MaximumSignatureBytes = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly IFileDownloader _inner;
    private readonly byte[] _publicKey;

    public SignedFileDownloader(
        IFileDownloader inner,
        string publicKeySubjectPublicKeyInfoBase64)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeySubjectPublicKeyInfoBase64);
        try
        {
            _publicKey = Convert.FromBase64String(publicKeySubjectPublicKeyInfoBase64);
            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(_publicKey, out int bytesRead);
            if (bytesRead != _publicKey.Length)
            {
                throw new CryptographicException("Trailing public key data was found.");
            }
        }
        catch (Exception exception) when (exception is
            FormatException or CryptographicException)
        {
            throw new ArgumentException(
                "The update signing public key is invalid.",
                nameof(publicKeySubjectPublicKeyInfoBase64),
                exception);
        }
    }

    public Task DownloadFile(
        string url,
        string localFile,
        Action<int> progress,
        IDictionary<string, string>? headers = null,
        double timeout = 30,
        CancellationToken cancellationToken = default) => _inner.DownloadFile(
            url,
            localFile,
            progress,
            headers,
            timeout,
            cancellationToken);

    public async Task<byte[]> DownloadBytes(
        string url,
        IDictionary<string, string>? headers = null,
        double timeout = 30)
    {
        if (!IsReleaseFeedUrl(url))
        {
            return await _inner.DownloadBytes(url, headers, timeout).ConfigureAwait(false);
        }

        return await DownloadAndVerifyFeedAsync(url, headers, timeout).ConfigureAwait(false);
    }

    public async Task<string> DownloadString(
        string url,
        IDictionary<string, string>? headers = null,
        double timeout = 30)
    {
        if (!IsReleaseFeedUrl(url))
        {
            return await _inner.DownloadString(url, headers, timeout).ConfigureAwait(false);
        }

        byte[] feed = await DownloadAndVerifyFeedAsync(url, headers, timeout)
            .ConfigureAwait(false);
        try
        {
            return StrictUtf8.GetString(feed);
        }
        catch (DecoderFallbackException exception)
        {
            throw new UpdateSignatureException("The signed update feed is not valid UTF-8.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(feed);
        }
    }

    internal static bool IsReleaseFeedUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string fileName = Path.GetFileName(uri.AbsolutePath);
        if (!fileName.StartsWith("releases.", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> channel = fileName.AsSpan(
            "releases.".Length,
            fileName.Length - "releases.".Length - ".json".Length);
        return !channel.IsEmpty && channel.Length <= 64 &&
            IsLowercaseChannelWithSeparators(channel);
    }

    internal static Uri GetSignatureUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new UpdateSignatureException("The update feed URL is invalid.");
        }

        UriBuilder builder = new(uri)
        {
            Path = uri.AbsolutePath + ".sig",
        };
        return builder.Uri;
    }

    private static bool IsLowercaseChannelWithSeparators(ReadOnlySpan<char> channel)
    {
        foreach (char character in channel)
        {
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private async Task<byte[]> DownloadAndVerifyFeedAsync(
        string url,
        IDictionary<string, string>? headers,
        double timeout)
    {
        byte[] feed = await _inner.DownloadBytes(url, headers, timeout).ConfigureAwait(false);
        byte[] signature = [];
        try
        {
            if (feed.Length is 0 or > MaximumFeedBytes)
            {
                throw new UpdateSignatureException("The update feed size is invalid.");
            }

            signature = await _inner.DownloadBytes(
                    GetSignatureUri(url).AbsoluteUri,
                    headers,
                    timeout)
                .ConfigureAwait(false);
            if (signature.Length is 0 or > MaximumSignatureBytes)
            {
                throw new UpdateSignatureException("The update signature size is invalid.");
            }

            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(_publicKey, out int bytesRead);
            bool verified = bytesRead == _publicKey.Length && verifier.VerifyData(
                feed,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            if (!verified)
            {
                throw new UpdateSignatureException("The update feed signature is invalid.");
            }

            return feed;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(feed);
            throw;
        }
        finally
        {
            if (signature.Length > 0)
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
    }
}
