using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Application.Sync;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Domain.Sync;

namespace SnapBoard.Infrastructure.Persistence;

public sealed partial class SqliteClipboardHistoryStore :
    IClipboardHistoryStore,
    IStorageMigrationBarrier,
    ISyncStore,
    IAsyncDisposable,
    IDisposable
{
    private const int InlinePayloadThresholdBytes = 64 * 1024;
    private const int OrphanCleanupBatchSize = 32;
    private static readonly TimeSpan OrphanCleanupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(24);
    private static readonly StringComparer BlobPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly ContentAddressedBlobStore _blobStore;
    private readonly SnapBoardDatabaseConnectionFactory _connectionFactory;
    private readonly SnapBoardDatabaseInitializer _initializer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _maintenance;
    private readonly object _initializationGate = new();
    private readonly SqliteWriteQueue _writeQueue;
    private Task<ClipboardHistoryInitializationResult>? _initializationTask;
    private Task? _orphanCleanupTask;
    private int _disposed;
    private int _migrationPrepared;

    public SqliteClipboardHistoryStore(
        SnapBoardStoragePaths paths,
        SnapBoardDatabaseConnectionFactory connectionFactory,
        SnapBoardDatabaseMigrator migrator)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _connectionFactory = connectionFactory;
        _initializer = new SnapBoardDatabaseInitializer(paths, connectionFactory, migrator);
        _blobStore = new ContentAddressedBlobStore(paths);
        _writeQueue = new SqliteWriteQueue(connectionFactory);
        _maintenance = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }

    public ValueTask<ClipboardHistoryInitializationResult> InitializeAsync(
        CancellationToken cancellationToken) => new(
            EnsureInitializedAsync(cancellationToken));

    public async ValueTask PrepareForMigrationAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _migrationPrepared, 1) != 0)
        {
            throw new InvalidOperationException("Storage migration is already prepared.");
        }

        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            _maintenance.Cancel();
            await _writeQueue.PauseAndDrainAsync(
                    CheckpointAndVerifyAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _migrationPrepared, 0);
            throw;
        }
    }

    public async ValueTask<ClipboardHistorySaveResult> SaveAsync(
        ClipboardCapturedItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _writeQueue.EnqueueAsync(
                (connection, token) => SaveCoreAsync(connection, item, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<ClipboardHistoryPage> SearchAsync(
        ClipboardHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunReadAsync(
            (connection, token) => SearchCoreAsync(connection, query, token),
            cancellationToken);
    }

    public ValueTask<ClipboardHistoryContent?> GetContentAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken) => RunReadAsync(
            (connection, token) => GetContentCoreAsync(connection, itemId, token),
            cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>> GetThumbnailAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken) => new(
            GetThumbnailCoreAsync(itemId, cancellationToken));

    public async ValueTask<bool> SetPinnedAsync(
        ClipboardItemId itemId,
        bool isPinned,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _writeQueue.EnqueueAsync(
                (connection, token) => SetPinnedCoreAsync(connection, itemId, isPinned, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> SetTagsAsync(
        ClipboardItemId itemId,
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        List<string> normalizedTags = ValidateTags(tags);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _writeQueue.EnqueueAsync(
                (connection, token) => SetTagsCoreAsync(
                    connection,
                    itemId,
                    normalizedTags,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> SoftDeleteAsync(
        ClipboardItemId itemId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _writeQueue.EnqueueAsync(
                (connection, token) => SoftDeleteCoreAsync(connection, itemId, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<int> ClearAsync(
        bool includePinned,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _writeQueue.EnqueueAsync(
                (connection, token) => ClearCoreAsync(connection, includePinned, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<int> ApplyRetentionAsync(
        ClipboardRetentionPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _writeQueue.EnqueueAsync(
                (connection, token) => ApplyRetentionCoreAsync(connection, policy, now, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> RecordUseAsync(
        ClipboardItemId itemId,
        DateTimeOffset usedAt,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _writeQueue.EnqueueAsync(
                (connection, token) => RecordUseCoreAsync(connection, itemId, usedAt, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<string?> GetSettingAsync(
        string key,
        CancellationToken cancellationToken) => RunReadAsync(
            (connection, token) => GetSettingCoreAsync(connection, key, token),
            cancellationToken);

    public async ValueTask SetSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                async (connection, token) =>
                {
                    await using SqliteTransaction transaction =
                        (SqliteTransaction)await connection.BeginTransactionAsync(token)
                            .ConfigureAwait(false);
                    try
                    {
                        await SetSettingCoreAsync(connection, transaction, key, value, token)
                            .ConfigureAwait(false);
                        if (SynchronizedSettingRegistry.IsSynchronized(key))
                        {
                            await AppendLocalSettingSyncEventAsync(
                                    connection,
                                    transaction,
                                    key,
                                    value,
                                    token)
                                .ConfigureAwait(false);
                        }

                        await transaction.CommitAsync(token).ConfigureAwait(false);
                        return true;
                    }
                    catch
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<int> CleanupOrphanedBlobsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - OrphanGracePeriod;
        IReadOnlyList<string> candidates = await FindOrphanCandidatesAsync(
                cutoff,
                cancellationToken)
            .ConfigureAwait(false);

        int removed = 0;
        foreach (string[] batch in candidates.Chunk(OrphanCleanupBatchSize))
        {
            removed += await _writeQueue.EnqueueAsync(
                    (connection, token) => CleanupOrphanedBlobBatchCoreAsync(
                        connection,
                        batch,
                        cutoff,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
            await Task.Yield();
        }

        await Task.Run(
                () => _blobStore.CleanupTemporaryFiles(
                    OrphanGracePeriod,
                    DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
        return removed;
    }

    public async ValueTask BulkImportAsync(
        IReadOnlyList<ClipboardCapturedItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeQueue.EnqueueAsync(
                async (connection, token) =>
                {
                    await BulkImportCoreAsync(connection, items, token).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
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

        _lifetime.Cancel();
        _maintenance.Cancel();
        if (_initializationTask is not null)
        {
            try
            {
                await _initializationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (SqliteException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_orphanCleanupTask is not null)
        {
            try
            {
                await _orphanCleanupTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }

        await _writeQueue.DisposeAsync().ConfigureAwait(false);
        _maintenance.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<ClipboardHistoryInitializationResult> EnsureInitializedAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Task<ClipboardHistoryInitializationResult> task;
        lock (_initializationGate)
        {
            task = _initializationTask ??= Task.Run(
                InitializeCoreAsync,
                CancellationToken.None);
        }

        return task.WaitAsync(cancellationToken);
    }

    private static async ValueTask<bool> CheckpointAndVerifyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using SqliteDataReader reader = await checkpoint
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("SQLite checkpoint returned no status.");
            }

            long busy = reader.GetInt64(0);
            long logFrames = reader.GetInt64(1);
            long checkpointedFrames = reader.GetInt64(2);
            if (busy != 0 || logFrames != checkpointedFrames)
            {
                throw new InvalidOperationException("SQLite WAL could not be fully checkpointed.");
            }
        }

        await using SqliteCommand integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA quick_check;";
        object? result = await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                Convert.ToString(result, CultureInfo.InvariantCulture),
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SQLite quick_check failed before migration.");
        }

        return true;
    }

    private async Task<ClipboardHistoryInitializationResult> InitializeCoreAsync()
    {
        ClipboardHistoryInitializationResult result = await _initializer
            .InitializeAsync(_lifetime.Token)
            .ConfigureAwait(false);
        // 完整目录扫描不进入首窗关键路径；延迟任务失败也不能影响数据库可用性。
        _orphanCleanupTask = Task.Run(RunDeferredOrphanCleanupAsync, CancellationToken.None);
        return result;
    }

    private async Task RunDeferredOrphanCleanupAsync()
    {
        try
        {
            await Task.Delay(OrphanCleanupDelay, _maintenance.Token).ConfigureAwait(false);
            await CleanupOrphanedBlobsAsync(_maintenance.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_maintenance.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Deferred clipboard blob cleanup failed with {0}.",
                exception.GetType().Name);
        }
    }

    private async Task<IReadOnlyList<string>> FindOrphanCandidatesAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        HashSet<string> referencedPaths = new(BlobPathComparer);
        await using (SqliteConnection connection =
            await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT relative_path FROM content_blobs;";
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                referencedPaths.Add(reader.GetString(0));
            }
        }

        return await Task.Run<IReadOnlyList<string>>(
                () =>
                {
                    List<string> candidates = [];
                    foreach (BlobFileEntry entry in _blobStore.EnumerateBlobEntries())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.LastWriteTimeUtc < cutoff &&
                            !referencedPaths.Contains(entry.RelativePath))
                        {
                            candidates.Add(entry.RelativePath);
                        }
                    }

                    return candidates;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<T> RunReadAsync<T>(
        Func<SqliteConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _migrationPrepared) != 0)
        {
            throw new InvalidOperationException("SQLite reads are paused for storage migration.");
        }

        return new ValueTask<T>(Task.Run(async () =>
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection =
                await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await operation(connection, cancellationToken).ConfigureAwait(false);
        }, cancellationToken));
    }

    private static List<string> ValidateTags(IReadOnlyCollection<string> tags)
    {
        return SyncConflictRules.NormalizeTags(tags).ToList();
    }
}
