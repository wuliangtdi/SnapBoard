using Microsoft.Data.Sqlite;

namespace SnapBoard.Infrastructure.Persistence;

/// <summary>
/// 创建短生命周期 SQLite 连接。调用方负责打开和释放连接；写操作随后会统一进入
/// 单写队列，避免多个后台任务争抢写锁或把数据库等待传递到 UI 线程。
/// </summary>
public sealed class SnapBoardDatabaseConnectionFactory
{
    private readonly string _connectionString;

    public SnapBoardDatabaseConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5,
        };

        _connectionString = builder.ToString();
    }

    public SqliteConnection CreateConnection() => new(_connectionString);
}
