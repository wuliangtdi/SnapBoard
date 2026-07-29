using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SnapBoard.Update.Velopack.Tests;

public sealed class CompositeUpdateSourceTests
{
    [Fact]
    public async Task DownloadFallsBackAcrossSourcesWithIdenticalSignedMetadata()
    {
        VelopackAsset officialAsset = CreateAsset();
        VelopackAsset githubAsset = CreateAsset();
        FakeUpdateSource official = new(officialAsset, downloadFailure: new IOException("offline"));
        FakeUpdateSource github = new(githubAsset, content: [1, 2, 3, 4]);
        CompositeUpdateSource source = new(
        [
            new UpdateSourceDescriptor("官方源", official),
            new UpdateSourceDescriptor("GitHub", github),
        ]);
        NullVelopackLogger logger = new();
        string target = Path.Combine(Path.GetTempPath(), $"snapboard-update-{Guid.NewGuid():N}.tmp");
        try
        {
            VelopackAssetFeed feed = await source.GetReleaseFeed(
                logger,
                "com.wuliangtdi.snapboard",
                "osx-stable");
            VelopackAsset selected = Assert.Single(feed.Assets);

            await source.DownloadReleaseEntry(
                logger,
                selected,
                target,
                _ => { },
                CancellationToken.None);

            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(target));
            Assert.Equal(1, official.DownloadCount);
            Assert.Equal(1, github.DownloadCount);
            Assert.Equal("GitHub", source.LastDownloadSource);
            Assert.Equal("官方源 / GitHub", source.GetSourceNames(selected));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task FeedRejectsSameReleaseWithDifferentHash()
    {
        VelopackAsset officialAsset = CreateAsset();
        VelopackAsset githubAsset = CreateAsset();
        githubAsset.SHA256 = new string('b', 64);
        CompositeUpdateSource source = new(
        [
            new UpdateSourceDescriptor("官方源", new FakeUpdateSource(officialAsset)),
            new UpdateSourceDescriptor("GitHub", new FakeUpdateSource(githubAsset)),
        ]);

        await Assert.ThrowsAsync<UpdateSourceConflictException>(() => source.GetReleaseFeed(
            new NullVelopackLogger(),
            "com.wuliangtdi.snapboard",
            "osx-stable"));
    }

    [Theory]
    [InlineData("../escape.nupkg")]
    [InlineData("folder/package.nupkg")]
    public async Task FeedRejectsUnsafePackageFileName(string fileName)
    {
        VelopackAsset asset = CreateAsset();
        asset.FileName = fileName;
        CompositeUpdateSource source = new(
            [new UpdateSourceDescriptor("GitHub", new FakeUpdateSource(asset))]);

        await Assert.ThrowsAsync<UpdateSourceConflictException>(() => source.GetReleaseFeed(
            new NullVelopackLogger(),
            "com.wuliangtdi.snapboard",
            "osx-stable"));
    }

    private static VelopackAsset CreateAsset() => new()
    {
        PackageId = "com.wuliangtdi.snapboard",
        Version = SemanticVersion.Parse("2.0.0"),
        Type = VelopackAssetType.Full,
        FileName = "SnapBoard-2.0.0-full.nupkg",
        SHA1 = new string('1', 40),
        SHA256 = new string('a', 64),
        Size = 4,
    };

    private sealed class FakeUpdateSource(
        VelopackAsset asset,
        byte[]? content = null,
        Exception? downloadFailure = null) : IUpdateSource
    {
        public int DownloadCount { get; private set; }

        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger,
            string? appId,
            string channel,
            Guid? stagingId = null,
            VelopackAsset? latestLocalRelease = null) => Task.FromResult(
                new VelopackAssetFeed { Assets = [asset] });

        public async Task DownloadReleaseEntry(
            IVelopackLogger logger,
            VelopackAsset releaseEntry,
            string localFile,
            Action<int> progress,
            CancellationToken cancellationToken)
        {
            DownloadCount++;
            if (downloadFailure is not null)
            {
                throw downloadFailure;
            }

            await File.WriteAllBytesAsync(localFile, content ?? [], cancellationToken);
            progress(100);
        }
    }
}
