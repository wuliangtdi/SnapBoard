namespace SnapBoard.Platform.Linux.Tests;

public sealed class LinuxPlatformAssemblyTests
{
    [Fact]
    public void PlatformIdentifierIsStable()
    {
        Assert.Equal("linux", LinuxPlatformAssembly.PlatformId);
    }
}
