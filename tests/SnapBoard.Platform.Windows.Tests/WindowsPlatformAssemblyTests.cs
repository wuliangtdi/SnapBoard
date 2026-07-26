namespace SnapBoard.Platform.Windows.Tests;

public sealed class WindowsPlatformAssemblyTests
{
    [Fact]
    public void PlatformIdentifierIsStable()
    {
        Assert.Equal("windows", WindowsPlatformAssembly.PlatformId);
    }
}
