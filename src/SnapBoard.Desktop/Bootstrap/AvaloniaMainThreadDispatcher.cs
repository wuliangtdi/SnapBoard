using Avalonia.Threading;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.Bootstrap;

internal sealed class AvaloniaMainThreadDispatcher : IPlatformMainThreadDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public T Invoke<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return CheckAccess()
            ? operation()
            : Dispatcher.UIThread.InvokeAsync(operation, DispatcherPriority.Send)
                .GetAwaiter()
                .GetResult();
    }

    public ValueTask<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return CheckAccess()
            ? ValueTask.FromResult(operation())
            : InvokeCoreAsync(operation, cancellationToken);
    }

    private static async ValueTask<T> InvokeCoreAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken) =>
        await Dispatcher.UIThread.InvokeAsync(
            operation,
            DispatcherPriority.Send,
            cancellationToken);
}
