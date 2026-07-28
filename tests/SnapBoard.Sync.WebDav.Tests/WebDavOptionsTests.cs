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
    public void ConstructorAllowsOnlyExplicitLoopbackHttpDevelopment()
    {
        WebDavOptions options = new(
            new Uri("http://127.0.0.1:8080/dav"),
            allowInsecureLoopback: true);

        Assert.True(options.AllowInsecureLoopback);
        Assert.Throws<ArgumentException>(() => new WebDavOptions(
            new Uri("http://dav.example.test"),
            allowInsecureLoopback: true));
    }

    [Fact]
    public void ConstructorRejectsCredentialsAndUnsafeRemoteRoot()
    {
        Assert.Throws<ArgumentException>(() => new WebDavOptions(
            new Uri("https://user:password@dav.example.test")));
        Assert.Throws<ArgumentException>(() => new WebDavOptions(
            new Uri("https://dav.example.test"),
            "SnapBoard/../escape"));
    }

    [Fact]
    public void ConstructorNormalizesRemoteRoot()
    {
        WebDavOptions options = new(new Uri("https://dav.example.test"), "/SnapBoard/v1/");

        Assert.Equal("SnapBoard/v1", options.RemoteRoot);
        Assert.Equal(new Uri("https://dav.example.test/"), options.Endpoint);
    }
}
