using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SnapBoard.Platform.Abstractions.Security;

namespace SnapBoard.Platform.MacOS.Security;

[SupportedOSPlatform("macos")]
public sealed class MacOSKeychainSecretStore : IPlatformSecretStore
{
    private const string ServiceName = "com.wuliangtdi.snapboard";
    private const int DuplicateItem = -25299;
    private const int ItemNotFound = -25300;
    private readonly object _gate = new();
    private readonly IMacOSKeychainNative _native;

    public MacOSKeychainSecretStore()
        : this(new MacOSKeychainNative())
    {
    }

    internal MacOSKeychainSecretStore(IMacOSKeychainNative native)
    {
        _native = native;
    }

    public ValueTask<PlatformSecretReadResult> ReadAsync(
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidName(name))
        {
            return ValueTask.FromResult(new PlatformSecretReadResult(
                PlatformSecretStoreStatus.InvalidName));
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint item = 0;
            try
            {
                int status = _native.Find(ServiceName, name, out byte[]? secret, out item);
                return ValueTask.FromResult(status == 0 && secret is not null
                    ? new PlatformSecretReadResult(PlatformSecretStoreStatus.Success, secret)
                    : new PlatformSecretReadResult(MapStatus(status), default, status));
            }
            finally
            {
                _native.ReleaseItem(item);
            }
        }
    }

    public ValueTask<PlatformSecretWriteResult> WriteAsync(
        string name,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidName(name) || secret.Length > 64 * 1024)
        {
            return ValueTask.FromResult(new PlatformSecretWriteResult(
                PlatformSecretStoreStatus.InvalidName));
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint item = 0;
            byte[]? existingSecret = null;
            try
            {
                int findStatus = _native.Find(
                    ServiceName,
                    name,
                    out existingSecret,
                    out item);
                int status = findStatus == 0
                    ? _native.Modify(item, secret.Span)
                    : findStatus == ItemNotFound
                        ? Add(name, secret.Span)
                        : findStatus;
                return ValueTask.FromResult(new PlatformSecretWriteResult(
                    MapStatus(status),
                    status));
            }
            finally
            {
                if (existingSecret is not null)
                {
                    CryptographicOperations.ZeroMemory(existingSecret);
                }

                _native.ReleaseItem(item);
            }
        }
    }

    public ValueTask<PlatformSecretWriteResult> DeleteAsync(
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidName(name))
        {
            return ValueTask.FromResult(new PlatformSecretWriteResult(
                PlatformSecretStoreStatus.InvalidName));
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint item = 0;
            byte[]? existingSecret = null;
            try
            {
                int findStatus = _native.Find(
                    ServiceName,
                    name,
                    out existingSecret,
                    out item);
                if (findStatus == ItemNotFound)
                {
                    return ValueTask.FromResult(new PlatformSecretWriteResult(
                        PlatformSecretStoreStatus.NotFound,
                        findStatus));
                }

                int status = findStatus == 0 ? _native.Delete(item) : findStatus;
                return ValueTask.FromResult(new PlatformSecretWriteResult(
                    MapStatus(status),
                    status));
            }
            finally
            {
                if (existingSecret is not null)
                {
                    CryptographicOperations.ZeroMemory(existingSecret);
                }

                _native.ReleaseItem(item);
            }
        }
    }

    private int Add(string name, ReadOnlySpan<byte> secret)
    {
        int status = _native.Add(ServiceName, name, secret, out nint item);
        try
        {
            if (status != DuplicateItem)
            {
                return status;
            }

            // 另一个调用方可能在 Find 与 Add 之间创建了条目；重新查找并修改一次。
            _native.ReleaseItem(item);
            item = 0;
            byte[]? existingSecret = null;
            try
            {
                int findStatus = _native.Find(ServiceName, name, out existingSecret, out item);
                return findStatus == 0 ? _native.Modify(item, secret) : findStatus;
            }
            finally
            {
                if (existingSecret is not null)
                {
                    CryptographicOperations.ZeroMemory(existingSecret);
                }
            }
        }
        finally
        {
            _native.ReleaseItem(item);
        }
    }

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            Encoding.UTF8.GetByteCount(name) > 512)
        {
            return false;
        }

        return !name.Any(char.IsControl);
    }

    private static PlatformSecretStoreStatus MapStatus(int status) => status switch
    {
        0 => PlatformSecretStoreStatus.Success,
        ItemNotFound => PlatformSecretStoreStatus.NotFound,
        -25293 or -25308 or -128 => PlatformSecretStoreStatus.AccessDenied,
        _ => PlatformSecretStoreStatus.Failed,
    };
}
