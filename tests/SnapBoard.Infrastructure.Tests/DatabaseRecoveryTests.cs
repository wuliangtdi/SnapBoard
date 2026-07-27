using Microsoft.Data.Sqlite;
using SnapBoard.Infrastructure.Persistence;

namespace SnapBoard.Infrastructure.Tests;

public sealed class DatabaseRecoveryTests
{
    [Fact]
    public async Task CorruptDatabaseIsBackedUpAndRecreated()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"SnapBoard.Infrastructure.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        SnapBoardStoragePaths paths = SnapBoardStoragePaths.Create(root);
        await File.WriteAllBytesAsync(paths.DatabasePath, [0x53, 0x6e, 0x61, 0x70, 0x42, 0x6f, 0x61, 0x72, 0x64]);

        await using HistoryStoreTestContext context = await HistoryStoreTestContext.CreateAsync(
            root,
            initialize: false);
        var result = await context.Store.InitializeAsync(CancellationToken.None);

        Assert.True(result.RecoveredCorruptDatabase);
        Assert.NotNull(result.RecoveryDirectory);
        Assert.NotNull(result.DiagnosticCode);
        Assert.True(File.Exists(Path.Combine(result.RecoveryDirectory!, "snapboard.db")));
        Assert.True(File.Exists(Path.Combine(result.RecoveryDirectory!, "recovery.txt")));

        await using SqliteConnection connection = await context.ConnectionFactory
            .OpenConnectionAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }
}
