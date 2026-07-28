namespace SnapBoard.Platform.Abstractions.Storage;

public interface IStoragePlatformService
{
    ValueTask<StoragePathInspection> InspectPathAsync(
        string path,
        bool probeWriteCapabilities,
        CancellationToken cancellationToken);

    ValueTask EnsurePrivateDirectoryAsync(
        string path,
        CancellationToken cancellationToken);

    StorageProcessIdentity GetCurrentProcessIdentity();

    ValueTask WaitForProcessExitAsync(
        StorageProcessIdentity process,
        CancellationToken cancellationToken);

    ValueTask<StorageProcessIdentity> StartProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    ValueTask StopProcessAsync(
        StorageProcessIdentity process,
        CancellationToken cancellationToken);

    bool OpenDirectory(string path);
}
