using System.Security.Cryptography;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Application.Sync;

public sealed record SyncRemoteConfiguration
{
    public const int MaximumEndpointCharacters = 1024;
    public const int MaximumRemoteRootCharacters = 1024;
    public const int MaximumUsernameCharacters = 256;

    public SyncRemoteConfiguration(
        Uri endpoint,
        string remoteRoot,
        string username,
        string? certificateSha256Pin = null,
        bool allowInsecureLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            endpoint.AbsoluteUri.Length > MaximumEndpointCharacters)
        {
            throw new ArgumentException(
                "The sync endpoint must be absolute and cannot contain credentials, query, or fragment.",
                nameof(endpoint));
        }

        bool secure = endpoint.Scheme == Uri.UriSchemeHttps;
        bool explicitLoopbackDevelopment =
            allowInsecureLoopback && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback;
        if (!secure && !explicitLoopbackDevelopment)
        {
            throw new ArgumentException(
                "The sync endpoint must use HTTPS outside explicit loopback development.",
                nameof(endpoint));
        }

        string normalizedRoot = remoteRoot.Trim('/');
        if (!IsValidRemoteRoot(normalizedRoot))
        {
            throw new ArgumentException("The sync remote root is invalid.", nameof(remoteRoot));
        }

        ArgumentNullException.ThrowIfNull(username);
        if (username.Length > MaximumUsernameCharacters || username.Any(char.IsControl))
        {
            throw new ArgumentException("The sync username is invalid.", nameof(username));
        }

        if (certificateSha256Pin is not null &&
            !SyncRemoteLayout.IsLowerHex(certificateSha256Pin, 64))
        {
            throw new ArgumentException(
                "The certificate pin must be lowercase SHA-256 hex.",
                nameof(certificateSha256Pin));
        }

        Endpoint = endpoint;
        RemoteRoot = normalizedRoot;
        Username = username;
        CertificateSha256Pin = certificateSha256Pin;
        AllowInsecureLoopback = explicitLoopbackDevelopment;
    }

    public Uri Endpoint { get; }

    public string RemoteRoot { get; }

    public string Username { get; }

    public string? CertificateSha256Pin { get; }

    public bool AllowInsecureLoopback { get; }

    private static bool IsValidRemoteRoot(string value)
    {
        if (value.Length is 0 or > MaximumRemoteRootCharacters || value.Contains('\\') ||
            value.Contains('?') || value.Contains('#'))
        {
            return false;
        }

        foreach (string segment in value.Split('/'))
        {
            if (segment.Length is 0 or > 128 || segment is "." or ".." ||
                segment.Any(character =>
                    character is not (>= 'a' and <= 'z') and
                    not (>= 'A' and <= 'Z') and
                    not (>= '0' and <= '9') and
                    not '-' and not '_' and not '.'))
            {
                return false;
            }
        }

        return true;
    }
}

public enum SyncCredentialOperationStatus
{
    Success = 0,
    NotFound = 1,
    AccessDenied = 2,
    Failed = 3,
}

public sealed class SyncCredentialLease : IDisposable
{
    private byte[]? _password;
    private SyncRemoteConfiguration? _remoteConfiguration;

    public SyncCredentialLease(
        SyncRemoteConfiguration remoteConfiguration,
        byte[] password)
    {
        _remoteConfiguration = remoteConfiguration ??
            throw new ArgumentNullException(nameof(remoteConfiguration));
        _password = password ?? throw new ArgumentNullException(nameof(password));
    }

    public SyncRemoteConfiguration RemoteConfiguration => _remoteConfiguration ??
        throw new ObjectDisposedException(nameof(SyncCredentialLease));

    public ReadOnlyMemory<byte> Password => _password ??
        throw new ObjectDisposedException(nameof(SyncCredentialLease));

    public void Dispose()
    {
        Interlocked.Exchange(ref _remoteConfiguration, null);
        byte[]? password = Interlocked.Exchange(ref _password, null);
        if (password is not null)
        {
            CryptographicOperations.ZeroMemory(password);
        }

        GC.SuppressFinalize(this);
    }
}

public sealed record SyncCredentialOpenResult(
    SyncCredentialOperationStatus Status,
    SyncCredentialLease? Credential = null);

public interface ISyncCredentialService
{
    ValueTask<SyncCredentialOperationStatus> StoreAsync(
        Guid spaceId,
        SyncRemoteConfiguration remoteConfiguration,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken);

    ValueTask<SyncCredentialOpenResult> OpenAsync(
        Guid spaceId,
        CancellationToken cancellationToken);

    ValueTask<SyncCredentialOperationStatus> DeleteAsync(
        Guid spaceId,
        CancellationToken cancellationToken);
}

public interface ISyncRecoveryMaterialStore
{
    ValueTask<string> SaveAsync(
        Guid spaceId,
        int keyVersion,
        ReadOnlyMemory<byte> recoveryEnvelope,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(
        Guid spaceId,
        int keyVersion,
        CancellationToken cancellationToken);
}

public interface ISyncObjectProtector
{
    byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        SyncObjectDescriptor descriptor,
        ReadOnlySpan<byte> masterKey);

    byte[] Decrypt(
        ReadOnlySpan<byte> encryptedEnvelope,
        SyncObjectDescriptor expectedDescriptor,
        ReadOnlySpan<byte> masterKey);

    SyncObjectDescriptor ReadDescriptor(ReadOnlySpan<byte> encryptedEnvelope);

    string ComputeKeyedBlobId(
        ReadOnlySpan<byte> masterKey,
        Guid spaceId,
        string plaintextSha256);
}

public enum SyncRemoteErrorCategory
{
    None = 0,
    Authentication = 1,
    Permission = 2,
    NotFound = 3,
    AlreadyExists = 4,
    RateLimited = 5,
    Transient = 6,
    Network = 7,
    Timeout = 8,
    Certificate = 9,
    Protocol = 10,
    ResponseTooLarge = 11,
}

public sealed record SyncRemoteResult(
    bool IsSuccess,
    SyncRemoteErrorCategory ErrorCategory,
    string? ETag = null,
    TimeSpan? RetryAfter = null,
    bool AlreadyExisted = false);

public sealed class SyncRemoteContentLease : IDisposable
{
    private byte[]? _content;

    public SyncRemoteContentLease(byte[] content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public ReadOnlyMemory<byte> Content => _content ??
        throw new ObjectDisposedException(nameof(SyncRemoteContentLease));

    public void Dispose()
    {
        byte[]? content = Interlocked.Exchange(ref _content, null);
        if (content is not null)
        {
            CryptographicOperations.ZeroMemory(content);
        }

        GC.SuppressFinalize(this);
    }
}

public sealed record SyncRemoteContentResult(
    SyncRemoteResult Result,
    SyncRemoteContentLease? Content = null);

public sealed record SyncRemoteDeviceListResult(
    SyncRemoteResult Result,
    IReadOnlyList<Guid> DeviceIds);

public sealed record SyncRemoteEventReference(
    Guid DeviceId,
    long Sequence,
    Guid EventId,
    string? ETag);

public sealed record SyncRemoteEventListResult(
    SyncRemoteResult Result,
    IReadOnlyList<SyncRemoteEventReference> Events);

public interface ISyncRemoteSession : IAsyncDisposable
{
    ValueTask<SyncRemoteResult> EnsureHierarchyAsync(
        Guid spaceId,
        Guid localDeviceId,
        CancellationToken cancellationToken);

    ValueTask<SyncRemoteContentResult> GetMetadataAsync(
        Guid spaceId,
        CancellationToken cancellationToken);

    ValueTask<SyncRemoteResult> PutMetadataAsync(
        Guid spaceId,
        ReadOnlyMemory<byte> encryptedMetadata,
        CancellationToken cancellationToken);

    ValueTask<SyncRemoteDeviceListResult> ListDevicesAsync(
        Guid spaceId,
        CancellationToken cancellationToken);

    ValueTask<SyncRemoteEventListResult> ListEventsAsync(
        Guid spaceId,
        Guid deviceId,
        CancellationToken cancellationToken);

    ValueTask<SyncRemoteContentResult> GetEventAsync(
        Guid spaceId,
        SyncRemoteEventReference remoteEvent,
        CancellationToken cancellationToken);

    ValueTask<SyncRemoteResult> PutEventAsync(
        Guid spaceId,
        Guid deviceId,
        long sequence,
        Guid eventId,
        ReadOnlyMemory<byte> encryptedEvent,
        CancellationToken cancellationToken);

    ValueTask<SyncRemoteContentResult> GetBlobAsync(
        Guid spaceId,
        string keyedBlobId,
        CancellationToken cancellationToken);

    ValueTask<SyncRemoteResult> PutBlobAsync(
        Guid spaceId,
        string keyedBlobId,
        ReadOnlyMemory<byte> encryptedBlob,
        CancellationToken cancellationToken);
}

public interface ISyncRemoteSessionFactory
{
    ISyncRemoteSession Create(
        SyncRemoteConfiguration configuration,
        ReadOnlyMemory<byte> password);
}
