using System.Security.Cryptography;

namespace SnapBoard.Infrastructure.Persistence;

internal sealed record StagedBlob(
    string Hash,
    string RelativePath,
    string MediaType,
    long SizeBytes,
    bool CreatedNew);

internal sealed record BlobFileEntry(
    string RelativePath,
    DateTimeOffset LastWriteTimeUtc);

internal sealed class ContentAddressedBlobStore(SnapBoardStoragePaths paths)
{
    public async ValueTask<StagedBlob> StageAsync(
        ReadOnlyMemory<byte> content,
        string mediaType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        string hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        string relativePath = $"{hash[..2]}/{hash}.blob";
        string finalPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        if (File.Exists(finalPath))
        {
            return new StagedBlob(hash, relativePath, mediaType, content.Length, false);
        }

        string temporaryDirectory = Path.Combine(paths.BlobDirectory, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        string temporaryPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.tmp");
        bool createdNew = false;
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, finalPath);
                createdNew = true;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // 同一内容可能被并发暂存；最终文件由哈希校验，可直接复用。
            }

            return new StagedBlob(hash, relativePath, mediaType, content.Length, createdNew);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        string path = GetFullPath(relativePath);
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : ReadOnlyMemory<byte>.Empty;
    }

    public ValueTask DeleteAsync(string relativePath)
    {
        string path = GetFullPath(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
            TryRemoveEmptyParent(path);
        }

        return ValueTask.CompletedTask;
    }

    public IEnumerable<BlobFileEntry> EnumerateBlobEntries()
    {
        if (!Directory.Exists(paths.BlobDirectory))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(
            paths.BlobDirectory,
            "*.blob",
            SearchOption.AllDirectories))
        {
            yield return new BlobFileEntry(
                NormalizeRelativePath(Path.GetRelativePath(paths.BlobDirectory, path)),
                new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
        }
    }

    public bool DeleteIfOlderThan(string relativePath, DateTimeOffset cutoff)
    {
        string path = GetFullPath(relativePath);
        if (!File.Exists(path) || new DateTimeOffset(File.GetLastWriteTimeUtc(path)) >= cutoff)
        {
            return false;
        }

        File.Delete(path);
        TryRemoveEmptyParent(path);
        return true;
    }

    public int CleanupTemporaryFiles(TimeSpan maximumAge, DateTimeOffset now)
    {
        string temporaryDirectory = Path.Combine(paths.BlobDirectory, ".tmp");
        if (!Directory.Exists(temporaryDirectory))
        {
            return 0;
        }

        int deleted = 0;
        foreach (string path in Directory.EnumerateFiles(temporaryDirectory, "*.tmp"))
        {
            if (now - new DateTimeOffset(File.GetLastWriteTimeUtc(path)) <= maximumAge)
            {
                continue;
            }

            File.Delete(path);
            deleted++;
        }

        return deleted;
    }

    private string GetFullPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string root = Path.GetFullPath(paths.BlobDirectory) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(paths.BlobDirectory, relativePath));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root, comparison))
        {
            throw new InvalidOperationException("Blob path escaped the configured root.");
        }

        return fullPath;
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/');

    private static void TryRemoveEmptyParent(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return;
        }

        try
        {
            Directory.Delete(parent, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
