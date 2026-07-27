using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SnapBoard.Platform.Windows.Desktop;

public enum SingleInstanceCommand : byte
{
    ActivateMainWindow = 1,
    ShowQuickWindow = 2,
    ShowSettingsWindow = 3,
    Exit = 4,
    RemainInBackground = 5,
}

/// <summary>
/// Windows 单实例协调器。互斥量只负责进程所有权，命名管道只传递固定的一字节命令，
/// 避免引入反射序列化、任意载荷或跨用户激活通道。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSingleInstanceCoordinator : IDisposable
{
    private static readonly TimeSpan[] ConnectBackoff =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
    ];

    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _commandGate = new();
    private readonly ConcurrentQueue<SingleInstanceCommand> _pendingCommands = new();
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private Task? _serverTask;
    private int _listening;
    private int _disposed;
    private Action<SingleInstanceCommand>? _commandReceived;

    private WindowsSingleInstanceCoordinator(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    public event Action<SingleInstanceCommand> CommandReceived
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_commandGate)
            {
                _commandReceived += value;
            }

            DrainPendingCommands();
        }
        remove
        {
            lock (_commandGate)
            {
                _commandReceived -= value;
            }
        }
    }

    public static bool TryAcquire(
        string applicationId,
        SingleInstanceCommand secondaryCommand,
        out WindowsSingleInstanceCoordinator? coordinator,
        out bool primaryNotified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);

        string userScope = GetUserScope();
        string safeApplicationId = SanitizeName(applicationId);
        string mutexName = $@"Local\{safeApplicationId}.{userScope}";
        string pipeName = $"{safeApplicationId}.{userScope}";
        Mutex mutex = new(initiallyOwned: false, mutexName);

        bool ownsMutex;
        try
        {
            ownsMutex = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // 上一个进程异常终止时内核会把互斥量标记为 abandoned；当前进程已经取得所有权，
            // 可以安全接管，而不是让用户永久卡在无法启动的状态。
            ownsMutex = true;
        }

        if (ownsMutex)
        {
            coordinator = new WindowsSingleInstanceCoordinator(mutex, pipeName);
            primaryNotified = false;
            return true;
        }

        mutex.Dispose();
        coordinator = null;
        primaryNotified = NotifyPrimaryAsync(pipeName, secondaryCommand).GetAwaiter().GetResult();
        return false;
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _listening, 1) != 0)
        {
            return;
        }

        _serverTask = Task.Run(() => RunServerAsync(_shutdown.Token));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            _serverTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // 命名 Mutex 的所有权绑定取得它的线程。Program 在进入 Avalonia 消息循环前取得，
            // 并在同一主线程的 finally 中释放，避免后台线程错误释放导致实例锁遗留。
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream server = new(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                byte[] commandBuffer = new byte[1];
                int bytesRead = await server.ReadAsync(commandBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 1 && Enum.IsDefined((SingleInstanceCommand)commandBuffer[0]))
                {
                    try
                    {
                        DispatchCommand((SingleInstanceCommand)commandBuffer[0]);
                    }
                    catch
                    {
                        // 跨进程命令回调不能终止管道监听。Desktop 处理器只应向 UI Dispatcher 投递。
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> NotifyPrimaryAsync(
        string pipeName,
        SingleInstanceCommand command)
    {
        foreach (TimeSpan delay in ConnectBackoff)
        {
            try
            {
                await using NamedPipeClientStream client = new(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification);
                using CancellationTokenSource timeout = new(delay);
                await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                byte[] commandBuffer = [(byte)command];
                await client.WriteAsync(commandBuffer, timeout.Token).ConfigureAwait(false);
                await client.FlushAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or OperationCanceledException)
            {
                // 主实例可能仍在加载 Native AOT 与 Avalonia。连接采用总时长有界的短退避，
                // 既覆盖启动竞争，也不会让第二实例无限挂起。
            }
        }

        return false;
    }

    private void DispatchCommand(SingleInstanceCommand command)
    {
        Action<SingleInstanceCommand>? handler;
        lock (_commandGate)
        {
            handler = _commandReceived;
            if (handler is null)
            {
                _pendingCommands.Enqueue(command);
                return;
            }
        }

        handler(command);
    }

    private void DrainPendingCommands()
    {
        while (_pendingCommands.TryDequeue(out SingleInstanceCommand command))
        {
            Action<SingleInstanceCommand>? handler;
            lock (_commandGate)
            {
                handler = _commandReceived;
                if (handler is null)
                {
                    _pendingCommands.Enqueue(command);
                    return;
                }
            }

            handler(command);
        }
    }

    private static string GetUserScope()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        return SanitizeName(string.IsNullOrWhiteSpace(sid) ? Environment.UserName : sid);
    }

    private static string SanitizeName(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        foreach (char character in value)
        {
            buffer[length++] = char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_';
        }

        return new string(buffer[..length]);
    }
}
