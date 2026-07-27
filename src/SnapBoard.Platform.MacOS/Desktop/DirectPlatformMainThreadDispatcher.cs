using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Platform.MacOS.Desktop;

/// <summary>
/// 探针和无 UI 单元测试使用的直通调度器。Desktop 生产进程必须注入 Avalonia 主线程调度器。
/// </summary>
internal sealed class DirectPlatformMainThreadDispatcher : IPlatformMainThreadDispatcher
{
    public static DirectPlatformMainThreadDispatcher Instance { get; } = new();

    public bool CheckAccess() => true;

    public T Invoke<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation();
    }

    public ValueTask<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operation());
    }
}
