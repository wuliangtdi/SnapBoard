namespace SnapBoard.Sync.WebDav.Tests;

public sealed class WebDavOptionsTests
{
    [Fact]
    public void ConstructorRejectsInsecureEndpoint()
    {
        static void CreateOptions() => _ = new WebDavOptions(new Uri("http://dav.example.test"));

        Assert.Throws<ArgumentException>(CreateOptions);
    }

    [Fact]
    public void ConstructorNormalizesRemoteRoot()
    {
        WebDavOptions options = new(new Uri("https://dav.example.test"), "/SnapBoard/v1/");

        Assert.Equal("SnapBoard/v1", options.RemoteRoot);
    }
}
