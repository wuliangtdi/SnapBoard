using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace SnapBoard.Infrastructure.Persistence;

/// <summary>
/// SQLite 写事务通过有界单读 Channel 串行执行。生产者等待容量而不是无限堆积，
/// 每个工作项使用独立连接，事务与连接都不会逃逸到 Application/UI。
/// </summary>
internal sealed class SqliteWriteQueue : IAsyncDisposable, IDisposable
{
    private readonly SnapBoardDatabaseConnectionFactory _connectionFactory;
    private readonly Channel<IWriteWorkItem> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _disposed;

    public SqliteWriteQueue(
        SnapBoardDatabaseConnectionFactory connectionFactory,
        int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _connectionFactory = connectionFactory;
        _channel = Channel.CreateBounded<IWriteWorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = Task.Run(() => RunAsync(_shutdown.Token), CancellationToken.None);
    }

    public async ValueTask<T> EnqueueAsync<T>(
        Func<SqliteConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(operation);
        WriteWorkItem<T> item = new(operation, cancellationToken);
        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        return await item.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (IWriteWorkItem item in
                _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await item.ExecuteAsync(_connectionFactory).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private interface IWriteWorkItem
    {
        ValueTask ExecuteAsync(SnapBoardDatabaseConnectionFactory connectionFactory);
    }

    private sealed class WriteWorkItem<T>(
        Func<SqliteConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken) : IWriteWorkItem
    {
        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ExecuteAsync(
            SnapBoardDatabaseConnectionFactory connectionFactory)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                await using SqliteConnection connection =
                    await connectionFactory.OpenConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);
                T result = await operation(connection, cancellationToken).ConfigureAwait(false);
                Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
        }
    }
}
