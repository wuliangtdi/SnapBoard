namespace SnapBoard.Application.Tests;

public sealed class ApplicationAssemblyTests
{
    [Fact]
    public void MarkerBelongsToApplicationAssembly()
    {
        Assert.Equal("SnapBoard.Application", typeof(ApplicationAssemblyMarker).Assembly.GetName().Name);
    }
}
