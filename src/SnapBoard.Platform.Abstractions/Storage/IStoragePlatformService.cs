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

    ValueTask EnsurePrivateDirectoryAsync(
        string path,
        StorageDirectorySecurityMode mode,
        CancellationToken cancellationToken) => EnsurePrivateDirectoryAsync(path, cancellationToken);

    StoragePathRelation GetPathRelation(string left, string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        string normalizedLeft = NormalizePath(left);
        string normalizedRight = NormalizePath(right);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (normalizedLeft.Equals(normalizedRight, comparison))
        {
            return StoragePathRelation.Same;
        }

        if (normalizedRight.StartsWith(
                normalizedLeft + Path.DirectorySeparatorChar,
                comparison))
        {
            return StoragePathRelation.Ancestor;
        }

        return normalizedLeft.StartsWith(
            normalizedRight + Path.DirectorySeparatorChar,
            comparison)
            ? StoragePathRelation.Descendant
            : StoragePathRelation.Unrelated;
    }

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

    private static string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
