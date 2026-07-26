using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Platform.MacOS.Clipboard;

internal sealed class MacOSClipboardEventQueue
{
    private readonly Channel<ClipboardChangedEvent> _channel;
    private long _droppedEventCount;

    public MacOSClipboardEventQueue(int capacity)
    {
        _channel = Channel.CreateBounded<ClipboardChangedEvent>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });
    }

    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    public bool TryWrite(ClipboardChangedEvent change)
    {
        if (_channel.Writer.TryWrite(change))
        {
            return true;
        }

        // 轮询 tick 只携带 changeCount 和时间戳。队列满时丢弃最旧事件，
        // 保留最新状态并记录背压计数，绝不在计时路径读取大对象或等待消费者。
        _channel.Reader.TryRead(out _);
        Interlocked.Increment(ref _droppedEventCount);
        return _channel.Writer.TryWrite(change);
    }

    public IAsyncEnumerable<ClipboardChangedEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
