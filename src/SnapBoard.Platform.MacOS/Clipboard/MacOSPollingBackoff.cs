namespace SnapBoard.Platform.MacOS.Clipboard;

internal sealed class MacOSPollingBackoff(MacOSClipboardSettings settings)
{
    private int _unchangedPolls;

    public TimeSpan CurrentInterval => _unchangedPolls >= settings.UnchangedPollsBeforeIdle
        ? settings.IdlePollingInterval
        : settings.ActivePollingInterval;

    public void RecordChanged() => _unchangedPolls = 0;

    public void RecordUnchanged()
    {
        if (_unchangedPolls < settings.UnchangedPollsBeforeIdle)
        {
            _unchangedPolls++;
        }
    }
}
