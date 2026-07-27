namespace SnapBoard.Platform.Abstractions.Desktop;

/// <summary>
/// 平台原生 UI 调用的主线程调度边界。平台适配器不得引用 Avalonia Dispatcher。
/// </summary>
public interface IPlatformMainThreadDispatcher
{
    bool CheckAccess();

    T Invoke<T>(Func<T> operation);

    ValueTask<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default);
}
