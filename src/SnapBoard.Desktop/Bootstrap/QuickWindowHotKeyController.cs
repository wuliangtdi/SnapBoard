using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.Bootstrap;

internal sealed class QuickWindowHotKeyController
{
    private readonly DoubleHotKeyPressStateMachine _doublePressStateMachine;
    private readonly Func<bool> _isCaptureActive;
    private readonly Func<bool> _isProtectionActive;
    private readonly Action _showQuickWindow;

    public QuickWindowHotKeyController(
        TimeSpan doubleTriggerInterval,
        Func<bool> isCaptureActive,
        Func<bool> isProtectionActive,
        Action showQuickWindow,
        TimeProvider? timeProvider = null)
    {
        _doublePressStateMachine = new DoubleHotKeyPressStateMachine(
            doubleTriggerInterval,
            timeProvider);
        _isCaptureActive = isCaptureActive;
        _isProtectionActive = isProtectionActive;
        _showQuickWindow = showQuickWindow;
    }

    public bool IsWaitingForSecondTrigger => _doublePressStateMachine.IsWaiting;

    public void HandleTrigger(GlobalHotKeyTriggeredEventArgs trigger)
    {
        if (_isCaptureActive())
        {
            Reset();
            return;
        }

        if (trigger.Source == GlobalHotKeySlot.Primary)
        {
            Reset();
            if (!_isProtectionActive())
            {
                _showQuickWindow();
            }

            return;
        }

        if (_isProtectionActive())
        {
            Reset();
            return;
        }

        if (_doublePressStateMachine.Trigger(trigger.IsRepeat) ==
            DoubleHotKeyPressResult.Completed)
        {
            _showQuickWindow();
        }
    }

    public void ShowExplicitly()
    {
        Reset();
        _showQuickWindow();
    }

    public void Reset() => _doublePressStateMachine.Reset();
}
