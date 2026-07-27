namespace SnapBoard.Platform.Abstractions.Security;

public enum PlatformSecretStoreStatus
{
    Success = 0,
    NotFound = 1,
    InvalidName = 2,
    AccessDenied = 3,
    Failed = 4,
    Unsupported = 5,
}

public sealed record PlatformSecretReadResult(
    PlatformSecretStoreStatus Status,
    ReadOnlyMemory<byte> Secret = default,
    int NativeErrorCode = 0);

public sealed record PlatformSecretWriteResult(
    PlatformSecretStoreStatus Status,
    int NativeErrorCode = 0);

/// <summary>
/// 供同步凭据、设备密钥和内容主密钥复用的系统凭据存储边界。
/// 调用方不得把 Secret 写入普通配置、日志或异常消息。
/// </summary>
public interface IPlatformSecretStore
{
    ValueTask<PlatformSecretReadResult> ReadAsync(
        string name,
        CancellationToken cancellationToken);

    ValueTask<PlatformSecretWriteResult> WriteAsync(
        string name,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken);

    ValueTask<PlatformSecretWriteResult> DeleteAsync(
        string name,
        CancellationToken cancellationToken);
}
