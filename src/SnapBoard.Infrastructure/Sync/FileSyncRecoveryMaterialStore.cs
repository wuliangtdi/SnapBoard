using SnapBoard.Application.Sync;
using SnapBoard.Infrastructure.Persistence;
using SnapBoard.Sync.Contracts;

namespace SnapBoard.Infrastructure.Sync;

public sealed class FileSyncRecoveryMaterialStore : ISyncRecoveryMaterialStore
{
    private readonly SnapBoardStoragePaths _paths;

    public FileSyncRecoveryMaterialStore(SnapBoardStoragePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async ValueTask<string> SaveAsync(
        Guid spaceId,
        int keyVersion,
        ReadOnlyMemory<byte> recoveryEnvelope,
        CancellationToken cancellationToken)
    {
        string path = GetPath(spaceId, keyVersion);
        if (recoveryEnvelope.IsEmpty ||
            recoveryEnvelope.Length > SyncProtocol.MaximumEncryptedEnvelopeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryEnvelope));
        }

        Directory.CreateDirectory(_paths.RecoveryDirectory);
        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(recoveryEnvelope, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    public ValueTask DeleteAsync(
        Guid spaceId,
        int keyVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(GetPath(spaceId, keyVersion));
        return ValueTask.CompletedTask;
    }

    private string GetPath(Guid spaceId, int keyVersion)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(spaceId, Guid.Empty);
        if (keyVersion is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(keyVersion));
        }

        return Path.Combine(
            _paths.RecoveryDirectory,
            $"sync-space-{spaceId:N}-v{keyVersion}.recovery");
    }
}
