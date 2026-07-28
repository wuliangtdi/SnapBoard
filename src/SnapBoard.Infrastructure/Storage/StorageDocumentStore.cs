using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SnapBoard.Infrastructure.Storage;

public sealed class StorageMetadataException(string message, Exception? innerException = null) :
    IOException(message, innerException);

internal static class StorageDocumentStore
{
    internal const int MaximumDocumentBytes = 64 * 1024;

    public static ValueTask<StorageLocationDocument?> ReadLocationAsync(
        string path,
        CancellationToken cancellationToken) => ReadSignedAsync(
        path,
        StorageJsonContext.Default.StorageLocationDocument,
        static document => document.Integrity,
        static (document, integrity) => document with { Integrity = integrity },
        cancellationToken);

    public static ValueTask WriteLocationAsync(
        string path,
        StorageLocationDocument document,
        CancellationToken cancellationToken) => WriteSignedAsync(
        path,
        document,
        StorageJsonContext.Default.StorageLocationDocument,
        static (value, integrity) => value with { Integrity = integrity },
        keepBackup: true,
        cancellationToken);

    public static ValueTask<StorageInstanceDocument?> ReadInstanceAsync(
        string path,
        CancellationToken cancellationToken) => ReadSignedAsync(
        path,
        StorageJsonContext.Default.StorageInstanceDocument,
        static document => document.Integrity,
        static (document, integrity) => document with { Integrity = integrity },
        cancellationToken);

    public static ValueTask WriteInstanceAsync(
        string path,
        StorageInstanceDocument document,
        CancellationToken cancellationToken) => WriteSignedAsync(
        path,
        document,
        StorageJsonContext.Default.StorageInstanceDocument,
        static (value, integrity) => value with { Integrity = integrity },
        keepBackup: false,
        cancellationToken);

    public static ValueTask<StorageMigrationManifest?> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken) => ReadSignedAsync(
        path,
        StorageJsonContext.Default.StorageMigrationManifest,
        static document => document.Integrity,
        static (document, integrity) => document with { Integrity = integrity },
        cancellationToken);

    public static ValueTask WriteManifestAsync(
        string path,
        StorageMigrationManifest document,
        CancellationToken cancellationToken) => WriteSignedAsync(
        path,
        document,
        StorageJsonContext.Default.StorageMigrationManifest,
        static (value, integrity) => value with { Integrity = integrity },
        keepBackup: false,
        cancellationToken);

    public static ValueTask<StorageMigrationStateDocument?> ReadMigrationStateAsync(
        string path,
        CancellationToken cancellationToken) => ReadSignedAsync(
        path,
        StorageJsonContext.Default.StorageMigrationStateDocument,
        static document => document.Integrity,
        static (document, integrity) => document with { Integrity = integrity },
        cancellationToken);

    public static ValueTask WriteMigrationStateAsync(
        string path,
        StorageMigrationStateDocument document,
        CancellationToken cancellationToken) => WriteSignedAsync(
        path,
        document,
        StorageJsonContext.Default.StorageMigrationStateDocument,
        static (value, integrity) => value with { Integrity = integrity },
        keepBackup: true,
        cancellationToken);

    public static ValueTask<StorageStartupAcknowledgementDocument?> ReadStartupAcknowledgementAsync(
        string path,
        CancellationToken cancellationToken) => ReadSignedAsync(
        path,
        StorageJsonContext.Default.StorageStartupAcknowledgementDocument,
        static document => document.Integrity,
        static (document, integrity) => document with { Integrity = integrity },
        cancellationToken);

    public static ValueTask WriteStartupAcknowledgementAsync(
        string path,
        StorageStartupAcknowledgementDocument document,
        CancellationToken cancellationToken) => WriteSignedAsync(
        path,
        document,
        StorageJsonContext.Default.StorageStartupAcknowledgementDocument,
        static (value, integrity) => value with { Integrity = integrity },
        keepBackup: false,
        cancellationToken);

    private static async ValueTask<T?> ReadSignedAsync<T>(
        string path,
        JsonTypeInfo<T> jsonTypeInfo,
        Func<T, string> getIntegrity,
        Func<T, string, T> withIntegrity,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureRegularFile(path);
        FileInfo information = new(path);
        if (information.Length is <= 0 or > MaximumDocumentBytes)
        {
            throw new StorageMetadataException("The storage metadata length is invalid.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            T document = JsonSerializer.Deserialize(bytes, jsonTypeInfo) ??
                throw new StorageMetadataException("The storage metadata is empty.");
            string integrity = getIntegrity(document);
            if (!IsLowerHexSha256(integrity))
            {
                throw new StorageMetadataException("The storage metadata integrity is invalid.");
            }

            byte[] unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(
                withIntegrity(document, string.Empty),
                jsonTypeInfo);
            byte[] expected = SHA256.HashData(unsignedBytes);
            byte[] actual = Convert.FromHexString(integrity);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                {
                    throw new StorageMetadataException(
                        "The storage metadata integrity check failed.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new StorageMetadataException("The storage metadata JSON is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async ValueTask WriteSignedAsync<T>(
        string path,
        T document,
        JsonTypeInfo<T> jsonTypeInfo,
        Func<T, string, T> withIntegrity,
        bool keepBackup,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        string canonicalPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(canonicalPath) ??
            throw new InvalidOperationException("The metadata path has no parent directory.");
        EnsureRegularDirectory(directory);

        T unsigned = withIntegrity(document, string.Empty);
        byte[] unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, jsonTypeInfo);
        string integrity = Convert.ToHexStringLower(SHA256.HashData(unsignedBytes));
        byte[] signedBytes = JsonSerializer.SerializeToUtf8Bytes(
            withIntegrity(document, integrity),
            jsonTypeInfo);
        if (signedBytes.Length > MaximumDocumentBytes)
        {
            throw new StorageMetadataException("The storage metadata is too large.");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(canonicalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                await stream.WriteAsync(signedBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(canonicalPath) && keepBackup)
            {
                string backupPath = $"{canonicalPath}.bak";
                File.Copy(canonicalPath, backupPath, overwrite: true);
                FlushFile(backupPath);
            }

            File.Move(temporaryPath, canonicalPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unsignedBytes);
            CryptographicOperations.ZeroMemory(signedBytes);
            TryDelete(temporaryPath);
        }
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void EnsureRegularFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).LinkTarget is not null)
        {
            throw new StorageMetadataException("Storage metadata cannot be a reparse point.");
        }
    }

    private static void EnsureRegularDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new DirectoryInfo(path).LinkTarget is not null)
        {
            throw new StorageMetadataException("The metadata directory cannot be a reparse point.");
        }
    }

    private static void FlushFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
