namespace SnapBoard.Platform.MacOS.Clipboard;

internal sealed class MacOSClipboardFeedbackGuard(int capacity = 32)
{
    private readonly object _gate = new();
    private readonly Queue<long> _order = [];
    private readonly HashSet<long> _pending = [];

    public void Remember(long changeCount)
    {
        lock (_gate)
        {
            if (!_pending.Add(changeCount))
            {
                return;
            }

            _order.Enqueue(changeCount);
            while (_order.Count > capacity)
            {
                _pending.Remove(_order.Dequeue());
            }
        }
    }

    public bool TryConsume(long changeCount)
    {
        lock (_gate)
        {
            return _pending.Remove(changeCount);
        }
    }
}
