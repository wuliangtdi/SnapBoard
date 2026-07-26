using SnapBoard.Platform.MacOS.Clipboard;

namespace SnapBoard.Platform.MacOS.Tests;

public sealed class MacOSPollingBackoffTests
{
    [Fact]
    public void UnchangedPollsEnterIdleAndChangeReturnsToActiveInterval()
    {
        MacOSClipboardSettings settings = CreateSettings(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(600),
            unchangedPollsBeforeIdle: 2);
        MacOSPollingBackoff backoff = new(settings);

        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.CurrentInterval);

        backoff.RecordUnchanged();
        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.CurrentInterval);

        backoff.RecordUnchanged();
        Assert.Equal(TimeSpan.FromMilliseconds(600), backoff.CurrentInterval);

        backoff.RecordChanged();
        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.CurrentInterval);
    }

    internal static MacOSClipboardSettings CreateSettings(
        TimeSpan? active = null,
        TimeSpan? idle = null,
        int unchangedPollsBeforeIdle = 2) => new(
            EventQueueCapacity: 4,
            MaximumPayloadBytes: 1024 * 1024,
            MaximumFileCount: 32,
            ActivePollingInterval: active ?? TimeSpan.FromMilliseconds(50),
            IdlePollingInterval: idle ?? TimeSpan.FromMilliseconds(200),
            UnchangedPollsBeforeIdle: unchangedPollsBeforeIdle,
            TargetActivationPollInterval: TimeSpan.FromMilliseconds(20),
            TargetActivationAttempts: 3);
}
