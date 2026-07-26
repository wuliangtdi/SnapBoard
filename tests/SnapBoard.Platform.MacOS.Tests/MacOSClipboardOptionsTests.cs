namespace SnapBoard.Platform.MacOS.Tests;

public sealed class MacOSClipboardOptionsTests
{
    [Fact]
    public void IdlePollingCannotBeFasterThanActivePolling()
    {
        MacOSClipboardOptions options = new()
        {
            ActivePollingInterval = TimeSpan.FromMilliseconds(200),
            IdlePollingInterval = TimeSpan.FromMilliseconds(100),
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.ToSettings());
    }
}
