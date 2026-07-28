using System.Diagnostics;
using SnapBoard.Application.Clipboard;
using SnapBoard.Application.Storage;
using SnapBoard.Platform.Abstractions.Storage;

namespace SnapBoard.Desktop.Bootstrap;

internal sealed class StorageStartupAcknowledgementCoordinator(
    string migrationId,
    IClipboardHistoryService historyService,
    IStorageManagementService storageManagementService,
    IStoragePlatformService storagePlatformService) : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _task;
    private int _disposed;
    private int _started;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _task = Task.Run(AcknowledgeAsync, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        if (_task is not null)
        {
            try
            {
                _task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcknowledgeAsync()
    {
        try
        {
            _ = await historyService.InitializeAsync(_cancellation.Token).ConfigureAwait(false);
            await storageManagementService.AcknowledgeStartupAsync(
                    migrationId,
                    storagePlatformService.GetCurrentProcessIdentity(),
                    _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(
                "Storage migration startup acknowledgement failed with {0}.",
                exception.GetType().Name);
        }
    }
}
