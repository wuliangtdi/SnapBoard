using SnapBoard.Infrastructure.Persistence;

namespace SnapBoard.Infrastructure.Tests;

internal sealed class HistoryStoreTestContext : IAsyncDisposable
{
    private HistoryStoreTestContext(
        string rootDirectory,
        SnapBoardStoragePaths paths,
        SnapBoardDatabaseConnectionFactory connectionFactory,
        SnapBoardDatabaseMigrator migrator,
        SqliteClipboardHistoryStore store,
        bool deleteOnDispose)
    {
        RootDirectory = rootDirectory;
        Paths = paths;
        ConnectionFactory = connectionFactory;
        Migrator = migrator;
        Store = store;
        DeleteOnDispose = deleteOnDispose;
    }

    public string RootDirectory { get; }

    public SnapBoardStoragePaths Paths { get; }

    public SnapBoardDatabaseConnectionFactory ConnectionFactory { get; }

    public SnapBoardDatabaseMigrator Migrator { get; }

    public SqliteClipboardHistoryStore Store { get; }

    private bool DeleteOnDispose { get; }

    public static async Task<HistoryStoreTestContext> CreateAsync(
        string? rootDirectory = null,
        bool initialize = true,
        bool deleteOnDispose = true)
    {
        string root = rootDirectory ?? Path.Combine(
            Path.GetTempPath(),
            $"SnapBoard.Infrastructure.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        SnapBoardStoragePaths paths = SnapBoardStoragePaths.Create(root);
        SnapBoardDatabaseConnectionFactory factory = new(paths.DatabasePath);
        SnapBoardDatabaseMigrator migrator = new();
        SqliteClipboardHistoryStore store = new(paths, factory, migrator);
        HistoryStoreTestContext context = new(
            root,
            paths,
            factory,
            migrator,
            store,
            deleteOnDispose);
        if (initialize)
        {
            await store.InitializeAsync(CancellationToken.None);
        }

        return context;
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!DeleteOnDispose)
        {
            return;
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                await Task.Delay(50);
            }
        }
    }
}
