using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Platform.Windows.Clipboard;

internal sealed class ClipboardEventQueue
{
    private readonly Channel<ClipboardChangedEvent> _channel;
    private long _droppedEventCount;

    public ClipboardEventQueue(int capacity)
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

        // 原生消息线程绝不能等待消费者。队列满时丢弃最旧的序列号并保留最新状态，
        // 同时累计可观测计数，后续稳定性测试可以明确发现背压而不是静默漏报。
        _channel.Reader.TryRead(out _);
        Interlocked.Increment(ref _droppedEventCount);
        return _channel.Writer.TryWrite(change);
    }

    public IAsyncEnumerable<ClipboardChangedEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
