using System.Threading.Channels;
using SnapBoard.Platform.Abstractions.Clipboard;
using SnapBoard.Platform.MacOS.Clipboard;

namespace SnapBoard.Platform.MacOS.Tests;

internal sealed class FakeMacOSPasteboardNative : IMacOSPasteboardNative
{
    private long _changeCount;

    public long ChangeCount
    {
        get => Interlocked.Read(ref _changeCount);
        set => Interlocked.Exchange(ref _changeCount, value);
    }

    public int ReadCount { get; private set; }

    public int WriteCount { get; private set; }

    public ClipboardReadResult ReadResult { get; set; } = new(
        ClipboardReadStatus.Success,
        new ClipboardContentSnapshot
        {
            SequenceNumber = 0,
            CapturedAt = DateTimeOffset.UnixEpoch,
            Source = new ClipboardSourceInfo(
                null,
                null,
                null,
                ClipboardSourceAccessStatus.Unknown),
        });

    public long GetChangeCount() => ChangeCount;

    public ClipboardReadResult Read(ClipboardChangedEvent change)
    {
        ReadCount++;
        return ReadResult;
    }

    public ClipboardWriteResult Write(ClipboardWriteRequest request)
    {
        WriteCount++;
        long sequence = Interlocked.Increment(ref _changeCount);
        return new ClipboardWriteResult(
            ClipboardWriteStatus.Success,
            MacOSClipboardSequence.ToPublicSequence(sequence),
            FeedbackMarkerWritten: true);
    }
}

internal sealed class ControlledAsyncDelay : IAsyncDelay
{
    private readonly Channel<TimeSpan> _requested = Channel.CreateUnbounded<TimeSpan>();
    private readonly Channel<bool> _releases = Channel.CreateUnbounded<bool>();

    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        _requested.Writer.TryWrite(delay);
        await _releases.Reader.ReadAsync(cancellationToken);
    }

    public ValueTask<TimeSpan> WaitForRequestAsync(CancellationToken cancellationToken) =>
        _requested.Reader.ReadAsync(cancellationToken);

    public void ReleaseNext() => _releases.Writer.TryWrite(true);
}

internal sealed class FakeMacOSPasteNative : IMacOSPasteNative
{
    public MacOSAutomaticPasteTarget? Target { get; set; } =
        new(101, "com.example.target", "Target");

    public bool TargetAvailable { get; set; } = true;

    public bool AccessibilityPermission { get; set; } = true;

    public bool ActivateResult { get; set; } = true;

    public int FrontmostProcessId { get; set; } = 101;

    public bool SendPasteResult { get; set; } = true;

    public bool SendPasteCalled { get; private set; }

    public MacOSAutomaticPasteTarget? CaptureForegroundTarget() => Target;

    public bool IsTargetAvailable(MacOSAutomaticPasteTarget target) => TargetAvailable;

    public bool HasAccessibilityPermission() => AccessibilityPermission;

    public bool Activate(MacOSAutomaticPasteTarget target) => ActivateResult;

    public int GetFrontmostProcessId() => FrontmostProcessId;

    public bool SendPasteShortcut()
    {
        SendPasteCalled = true;
        return SendPasteResult;
    }
}

internal sealed class FakeMacOSClipboardSourceReader : IMacOSClipboardSourceReader
{
    public ClipboardSourceInfo Result { get; set; } = new(
        101,
        "测试应用",
        "/Applications/Test.app/Contents/MacOS/Test",
        ClipboardSourceAccessStatus.Identified,
        AttributionKind: ClipboardSourceAttributionKind.ForegroundWindowAtChange);

    public int CallCount { get; private set; }

    public int? ProcessId { get; private set; }

    public ClipboardSourceAttributionKind AttributionKind { get; private set; }

    public ClipboardSourceInfo Read(
        int? processId,
        ClipboardSourceAttributionKind attributionKind)
    {
        CallCount++;
        ProcessId = processId;
        AttributionKind = attributionKind;
        return Result;
    }
}
