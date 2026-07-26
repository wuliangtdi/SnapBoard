using SnapBoard.Platform.Windows.Clipboard;

namespace SnapBoard.Platform.Windows.Tests;

public sealed class ClipboardRetryPolicyTests
{
    [Fact]
    public void StopsAfterConfiguredRetries()
    {
        int attempts = 0;
        List<TimeSpan> waits = [];
        TimeSpan[] delays =
        [
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(10),
        ];

        bool opened = ClipboardRetryPolicy.Try(
            () =>
            {
                attempts++;
                return false;
            },
            delays,
            CancellationToken.None,
            (delay, _) => waits.Add(delay));

        Assert.False(opened);
        Assert.Equal(3, attempts);
        Assert.Equal(delays, waits);
    }

    [Fact]
    public void ReturnsImmediatelyAfterSuccessfulAttempt()
    {
        int attempts = 0;
        int waits = 0;

        bool opened = ClipboardRetryPolicy.Try(
            () => ++attempts == 2,
            [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10)],
            CancellationToken.None,
            (_, _) => waits++);

        Assert.True(opened);
        Assert.Equal(2, attempts);
        Assert.Equal(1, waits);
    }
}
