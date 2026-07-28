using SnapBoard.Application.Storage;
using SnapBoard.Infrastructure.Persistence;

namespace SnapBoard.Infrastructure.Storage;

internal static class StorageUsageCalculator
{
    public static ValueTask<StorageUsage> CalculateAsync(
        SnapBoardStoragePaths paths,
        CancellationToken cancellationToken) => new(Task.Run(
        () => Calculate(paths, cancellationToken),
        cancellationToken));

    private static StorageUsage Calculate(
        SnapBoardStoragePaths paths,
        CancellationToken cancellationToken)
    {
        long databaseBytes = GetFileLength(paths.DatabasePath);
        long blobBytes = CalculateDirectorySize(paths.BlobDirectory, cancellationToken);
        long recoveryBytes = CalculateDirectorySize(paths.RecoveryDirectory, cancellationToken);
        return new StorageUsage(databaseBytes, blobBytes, recoveryBytes);
    }

    private static long CalculateDirectorySize(
        string directory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        Stack<string> pending = new();
        pending.Push(Path.GetFullPath(directory));
        while (pending.TryPop(out string? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasReparsePoint(current))
            {
                throw new StorageMetadataException(
                    "A storage directory contains a reparse point.");
            }

            foreach (string file in Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasReparsePoint(file))
                {
                    throw new StorageMetadataException(
                        "A storage file contains a reparse point.");
                }

                total = checked(total + new FileInfo(file).Length);
            }

            foreach (string child in Directory.EnumerateDirectories(current))
            {
                pending.Push(child);
            }
        }

        return total;
    }

    private static long GetFileLength(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
