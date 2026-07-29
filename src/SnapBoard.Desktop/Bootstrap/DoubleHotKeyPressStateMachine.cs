namespace SnapBoard.Desktop.Bootstrap;

internal enum DoubleHotKeyPressResult
{
    WaitingForSecondTrigger = 0,
    Completed = 1,
    IgnoredRepeat = 2,
}

internal sealed class DoubleHotKeyPressStateMachine
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _triggerInterval;
    private readonly object _gate = new();
    private long? _firstTriggerTimestamp;

    public DoubleHotKeyPressStateMachine(
        TimeSpan triggerInterval,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(triggerInterval, TimeSpan.Zero);

        _triggerInterval = triggerInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsWaiting
    {
        get
        {
            lock (_gate)
            {
                return _firstTriggerTimestamp is not null;
            }
        }
    }

    public DoubleHotKeyPressResult Trigger(bool isRepeat = false)
    {
        lock (_gate)
        {
            if (isRepeat)
            {
                return DoubleHotKeyPressResult.IgnoredRepeat;
            }

            long now = _timeProvider.GetTimestamp();
            if (_firstTriggerTimestamp is not long first ||
                _timeProvider.GetElapsedTime(first, now) > _triggerInterval)
            {
                _firstTriggerTimestamp = now;
                return DoubleHotKeyPressResult.WaitingForSecondTrigger;
            }

            _firstTriggerTimestamp = null;
            return DoubleHotKeyPressResult.Completed;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _firstTriggerTimestamp = null;
        }
    }
}
