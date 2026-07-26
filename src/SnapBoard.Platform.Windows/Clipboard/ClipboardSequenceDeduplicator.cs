namespace SnapBoard.Platform.Windows.Clipboard;

internal sealed class ClipboardSequenceDeduplicator
{
    private uint _lastSequence;
    private bool _hasSequence;

    public bool TryAccept(uint sequenceNumber)
    {
        if (sequenceNumber == 0)
        {
            return false;
        }

        if (_hasSequence && sequenceNumber == _lastSequence)
        {
            return false;
        }

        // Windows 使用 DWORD 序列号，系统会在长期运行后回绕，因此这里只排除相邻重复，
        // 不用大小关系判断“新旧”，避免回绕后永久拒绝正常事件。
        _lastSequence = sequenceNumber;
        _hasSequence = true;
        return true;
    }
}
