using Microsoft.Data.Sqlite;

namespace SnapBoard.Infrastructure.Persistence;

/// <summary>
/// 创建短生命周期 SQLite 连接。调用方负责打开和释放连接；写操作随后会统一进入
/// 单写队列，避免多个后台任务争抢写锁或把数据库等待传递到 UI 线程。
/// </summary>
public sealed class SnapBoardDatabaseConnectionFactory
{
    private readonly string _connectionString;

    static SnapBoardDatabaseConnectionFactory()
    {
        // 直接调用 bundle 初始化入口，避免 Microsoft.Data.Sqlite 在裁剪/AOT 后依赖
        // 反射发现 SQLitePCL.Batteries_V2。CLR 保证静态构造只执行一次且并发安全。
        SQLitePCL.Batteries_V2.Init();
    }

    public SnapBoardDatabaseConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5,
        };

        _connectionString = builder.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection CreateConnection() => new(_connectionString);

    /// <summary>
    /// 仅清理当前数据库连接字符串对应的池，避免迁移或恢复一个存储位置时
    /// 关闭进程内其他独立数据库仍在使用的连接。
    /// </summary>
    public void ClearPool()
    {
        using SqliteConnection connection = CreateConnection();
        SqliteConnection.ClearPool(connection);
    }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

}
