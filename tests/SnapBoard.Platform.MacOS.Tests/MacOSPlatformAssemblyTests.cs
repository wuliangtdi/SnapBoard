namespace SnapBoard.Platform.MacOS.Tests;

public sealed class MacOSPlatformAssemblyTests
{
    [Fact]
    public void PlatformIdentifierIsStable()
    {
        Assert.Equal("macos", MacOSPlatformAssembly.PlatformId);
    }
}
