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
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _accepting = 1;
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
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _accepting) == 0)
            {
                throw new InvalidOperationException(
                    "SQLite writes are paused for storage migration.");
            }

            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _enqueueGate.Release();
        }

        return await item.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<T> PauseAndDrainAsync<T>(
        Func<SqliteConnection, CancellationToken, ValueTask<T>> finalOperation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(finalOperation);
        WriteWorkItem<T> item = new(finalOperation, cancellationToken);
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _accepting, 0) == 0)
            {
                throw new InvalidOperationException(
                    "SQLite writes are already paused for storage migration.");
            }

            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _enqueueGate.Release();
        }

        try
        {
            return await item.Completion.Task.ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _accepting, 1);
            throw;
        }
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
            _enqueueGate.Dispose();
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
