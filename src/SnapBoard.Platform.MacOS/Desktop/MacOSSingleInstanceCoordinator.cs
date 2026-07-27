using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Desktop;

/// <summary>
/// macOS 每用户单实例协调器。Unix 域套接字同时承担所有权和固定一字节命令通道，
/// 目录权限限制为当前用户，避免跨用户激活或任意载荷进入桌面进程。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSSingleInstanceCoordinator : IDisposable
{
    private const byte CommandAccepted = 0xA5;
    private const int ExclusiveLock = 2;
    private const int NonBlockingLock = 4;
    private const int MaximumPendingCommands = 16;
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMilliseconds(500);
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
    private readonly Queue<SingleInstanceCommand> _pendingCommands = new();
    private readonly Socket _listener;
    private readonly FileStream _ownershipLock;
    private readonly string _socketPath;
    private Task? _serverTask;
    private Action<SingleInstanceCommand>? _commandReceived;
    private int _listening;
    private int _disposed;

    private MacOSSingleInstanceCoordinator(
        Socket listener,
        FileStream ownershipLock,
        string socketPath)
    {
        _listener = listener;
        _ownershipLock = ownershipLock;
        _socketPath = socketPath;
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
        out MacOSSingleInstanceCoordinator? coordinator,
        out bool primaryNotified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        string socketPath = CreateSocketPath(applicationId);
        string lockPath = $"{socketPath}.lock";

        if (TryAcquireOwnership(lockPath, out FileStream? ownershipLock))
        {
            try
            {
                // 所有权锁由内核在进程退出时自动释放；持锁后可以安全删除崩溃遗留的 socket 节点，
                // 不会因首实例尚未开始 Accept 或暂时没有确认而误删活跃实例的命令通道。
                TryDeleteSocket(socketPath);
                if (TryCreateListener(socketPath, out Socket? listener))
                {
                    coordinator = new MacOSSingleInstanceCoordinator(
                        listener!,
                        ownershipLock!,
                        socketPath);
                    ownershipLock = null;
                    primaryNotified = false;
                    return true;
                }
            }
            finally
            {
                ownershipLock?.Dispose();
            }
        }

        coordinator = null;
        primaryNotified = NotifyPrimaryAsync(socketPath, secondaryCommand).GetAwaiter().GetResult();
        return false;
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _listening, 1) != 0)
        {
            return;
        }

        _serverTask = Task.Run(() => RunServerAsync(_shutdown.Token), CancellationToken.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _listener.Dispose();
        try
        {
            _serverTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            TryDeleteSocket(_socketPath);
            _ownershipLock.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using Socket connection = await _listener.AcceptAsync(cancellationToken)
                    .ConfigureAwait(false);
                byte[] commandBuffer = new byte[1];
                using CancellationTokenSource receiveCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveCancellation.CancelAfter(ReceiveTimeout);
                int bytesRead;
                try
                {
                    bytesRead = await connection.ReceiveAsync(
                            commandBuffer,
                            SocketFlags.None,
                            receiveCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 已连接但不发送命令的客户端不能永久占用串行监听循环。
                    continue;
                }

                if (bytesRead == 1 && Enum.IsDefined((SingleInstanceCommand)commandBuffer[0]))
                {
                    bool accepted = false;
                    try
                    {
                        DispatchCommand((SingleInstanceCommand)commandBuffer[0]);
                        accepted = true;
                    }
                    catch
                    {
                        // 跨进程命令回调失败不能终止监听；Desktop 处理器只应投递 UI 命令。
                    }

                    if (accepted)
                    {
                        byte[] acknowledgement = [CommandAccepted];
                        await connection.SendAsync(
                                acknowledgement,
                                SocketFlags.None,
                                receiveCancellation.Token)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> NotifyPrimaryAsync(
        string socketPath,
        SingleInstanceCommand command)
    {
        foreach (TimeSpan delay in ConnectBackoff)
        {
            try
            {
                using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                using CancellationTokenSource timeout = new(delay);
                await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token)
                    .ConfigureAwait(false);
                byte[] commandBuffer = [(byte)command];
                int bytesSent = await client.SendAsync(
                        commandBuffer,
                        SocketFlags.None,
                        timeout.Token)
                    .ConfigureAwait(false);
                if (bytesSent != commandBuffer.Length)
                {
                    continue;
                }

                byte[] acknowledgement = new byte[1];
                int bytesRead = await client.ReceiveAsync(
                        acknowledgement,
                        SocketFlags.None,
                        timeout.Token)
                    .ConfigureAwait(false);
                return bytesRead == 1 && acknowledgement[0] == CommandAccepted;
            }
            catch (Exception exception) when (
                exception is SocketException or TimeoutException or OperationCanceledException)
            {
                // 主实例可能正在加载 AOT 和 Avalonia；总时长有界的短退避覆盖启动竞争。
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
                if (_pendingCommands.Count == MaximumPendingCommands)
                {
                    _pendingCommands.Dequeue();
                }

                _pendingCommands.Enqueue(command);
                return;
            }
        }

        handler(command);
    }

    private void DrainPendingCommands()
    {
        while (true)
        {
            Action<SingleInstanceCommand>? handler;
            SingleInstanceCommand command;
            lock (_commandGate)
            {
                handler = _commandReceived;
                if (handler is null || _pendingCommands.Count == 0)
                {
                    return;
                }

                command = _pendingCommands.Dequeue();
            }

            handler(command);
        }
    }

    private static bool TryAcquireOwnership(
        string lockPath,
        out FileStream? ownershipLock)
    {
        FileStream candidate;
        try
        {
            candidate = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            ownershipLock = null;
            return false;
        }

        try
        {
            int fileDescriptor = candidate.SafeFileHandle.DangerousGetHandle().ToInt32();
            if (MacOSNativeMethods.Flock(
                    fileDescriptor,
                    ExclusiveLock | NonBlockingLock) != 0)
            {
                candidate.Dispose();
                ownershipLock = null;
                return false;
            }

            ownershipLock = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or OverflowException)
        {
            candidate.Dispose();
            ownershipLock = null;
            return false;
        }
    }

    private static bool TryCreateListener(string socketPath, out Socket? listener)
    {
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            listener.Dispose();
            listener = null;
            return false;
        }
    }

    private static string CreateSocketPath(string applicationId)
    {
        string directory = Path.Combine(Path.GetTempPath(), "snapboard-instance");
        Directory.CreateDirectory(directory);
        try
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Environment.UserName}\n{applicationId}"));
        string name = Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        return Path.Combine(directory, $"{name}.sock");
    }

    private static void TryDeleteSocket(string socketPath)
    {
        try
        {
            File.Delete(socketPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
