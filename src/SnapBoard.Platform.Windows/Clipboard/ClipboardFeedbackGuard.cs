namespace SnapBoard.Platform.Windows.Clipboard;

internal sealed class ClipboardFeedbackGuard(int capacity = 32)
{
    private readonly object _gate = new();
    private readonly HashSet<uint> _pending = [];
    private readonly Queue<uint> _order = [];

    public void Remember(uint sequenceNumber)
    {
        if (sequenceNumber == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_pending.Add(sequenceNumber))
            {
                return;
            }

            _order.Enqueue(sequenceNumber);
            while (_order.Count > capacity)
            {
                _pending.Remove(_order.Dequeue());
            }
        }
    }

    public bool TryConsume(uint sequenceNumber)
    {
        lock (_gate)
        {
            return _pending.Remove(sequenceNumber);
        }
    }
}
