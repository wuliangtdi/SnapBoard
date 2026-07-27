using System.Runtime.Versioning;
using System.Text;
using SnapBoard.Platform.Abstractions.Security;
using SnapBoard.Platform.Windows.Interop;

namespace SnapBoard.Platform.Windows.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSecretStore : IPlatformSecretStore
{
    private const string TargetPrefix = "com.wuliangtdi.snapboard/";
    private readonly object _gate = new();
    private readonly IWindowsCredentialNative _native;

    public WindowsCredentialSecretStore()
        : this(new WindowsCredentialNative())
    {
    }

    internal WindowsCredentialSecretStore(IWindowsCredentialNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
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
            int error = _native.Read(GetTargetName(name), out byte[]? secret);
            return ValueTask.FromResult(error == 0 && secret is not null
                ? new PlatformSecretReadResult(PlatformSecretStoreStatus.Success, secret)
                : new PlatformSecretReadResult(MapStatus(error), default, error));
        }
    }

    public ValueTask<PlatformSecretWriteResult> WriteAsync(
        string name,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidName(name) ||
            secret.Length > WindowsNativeConstants.MaximumCredentialBlobSize)
        {
            return ValueTask.FromResult(new PlatformSecretWriteResult(
                PlatformSecretStoreStatus.InvalidName));
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int error = _native.Write(GetTargetName(name), secret.Span);
            return ValueTask.FromResult(new PlatformSecretWriteResult(MapStatus(error), error));
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
            int error = _native.Delete(GetTargetName(name));
            return ValueTask.FromResult(new PlatformSecretWriteResult(MapStatus(error), error));
        }
    }

    private static string GetTargetName(string name) => TargetPrefix + name;

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            Encoding.UTF8.GetByteCount(name) > 512)
        {
            return false;
        }

        return !name.Any(char.IsControl);
    }

    private static PlatformSecretStoreStatus MapStatus(int error) => error switch
    {
        0 => PlatformSecretStoreStatus.Success,
        WindowsNativeConstants.ErrorNotFound => PlatformSecretStoreStatus.NotFound,
        WindowsNativeConstants.ErrorAccessDenied or
            WindowsNativeConstants.ErrorCancelled or
            WindowsNativeConstants.ErrorNoSuchLogonSession =>
            PlatformSecretStoreStatus.AccessDenied,
        _ => PlatformSecretStoreStatus.Failed,
    };
}
