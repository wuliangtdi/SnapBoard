using System.Globalization;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;

namespace SnapBoard.Infrastructure.Persistence;

public sealed class SnapBoardDatabaseInitializer(
    SnapBoardStoragePaths paths,
    SnapBoardDatabaseConnectionFactory connectionFactory,
    SnapBoardDatabaseMigrator migrator)
{
    public async ValueTask<ClipboardHistoryInitializationResult> InitializeAsync(
        CancellationToken cancellationToken)
    {
        CreateDirectories();
        ClipboardHistoryInitializationResult recovery =
            await RecoverIfCorruptAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection =
            await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
        await migrator.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
        return recovery;
    }

    private void CreateDirectories()
    {
        Directory.CreateDirectory(paths.RootDirectory);
        Directory.CreateDirectory(paths.BlobDirectory);
        Directory.CreateDirectory(paths.RecoveryDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                paths.RootDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async ValueTask<ClipboardHistoryInitializationResult> RecoverIfCorruptAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.DatabasePath) || new FileInfo(paths.DatabasePath).Length == 0)
        {
            return new ClipboardHistoryInitializationResult(false);
        }

        string? diagnosticCode = null;
        try
        {
            await using SqliteConnection connection =
                await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(
                Convert.ToString(result, CultureInfo.InvariantCulture),
                "ok",
                StringComparison.OrdinalIgnoreCase))
            {
                return new ClipboardHistoryInitializationResult(false);
            }

            diagnosticCode = "quick-check-failed";
        }
        catch (SqliteException exception) when (IsCorruption(exception))
        {
            diagnosticCode = $"sqlite-{exception.SqliteErrorCode}";
        }

        if (diagnosticCode is null)
        {
            return new ClipboardHistoryInitializationResult(false);
        }

        string recoveryDirectory = await BackupCorruptDatabaseAsync(
                diagnosticCode,
                cancellationToken)
            .ConfigureAwait(false);
        return new ClipboardHistoryInitializationResult(
            true,
            recoveryDirectory,
            diagnosticCode);
    }

    private async ValueTask<string> BackupCorruptDatabaseAsync(
        string diagnosticCode,
        CancellationToken cancellationToken)
    {
        // 先清空连接池再移动 db/wal/shm，避免 Windows 上仍有 Provider 句柄占用。
        SqliteConnection.ClearAllPools();
        string directoryName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        string recoveryDirectory = Path.Combine(paths.RecoveryDirectory, directoryName);
        Directory.CreateDirectory(recoveryDirectory);

        foreach (string source in new[]
        {
            paths.DatabasePath,
            $"{paths.DatabasePath}-wal",
            $"{paths.DatabasePath}-shm",
        })
        {
            if (!File.Exists(source))
            {
                continue;
            }

            string destination = Path.Combine(recoveryDirectory, Path.GetFileName(source));
            File.Move(source, destination);
        }

        string diagnosticPath = Path.Combine(recoveryDirectory, "recovery.txt");
        await File.WriteAllTextAsync(
                diagnosticPath,
                $"utc={DateTimeOffset.UtcNow:O}{Environment.NewLine}code={diagnosticCode}{Environment.NewLine}",
                cancellationToken)
            .ConfigureAwait(false);
        return recoveryDirectory;
    }

    private static bool IsCorruption(SqliteException exception) =>
        exception.SqliteErrorCode is 11 or 26;

    private static async ValueTask ConfigureDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode = WAL;";
        object? mode = await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
            Convert.ToString(mode, CultureInfo.InvariantCulture),
            "wal",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SQLite WAL mode could not be enabled.");
        }

        await using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText = """
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA wal_autocheckpoint = 1000;
            """;
        await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
