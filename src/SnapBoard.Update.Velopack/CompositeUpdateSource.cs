using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SnapBoard.Update.Velopack;

internal sealed record UpdateSourceDescriptor(string Name, IUpdateSource Source);

internal sealed class CompositeUpdateSource(
    IReadOnlyList<UpdateSourceDescriptor> sources) : IUpdateSource
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<UpdateSourceDescriptor> _sources =
        sources is { Count: > 0 }
            ? sources
            : throw new ArgumentException("At least one update source is required.", nameof(sources));
    private Dictionary<AssetKey, IReadOnlyList<AssetCandidate>> _candidates =
        new Dictionary<AssetKey, IReadOnlyList<AssetCandidate>>();
    private string? _lastDownloadSource;

    internal string? LastDownloadSource
    {
        get
        {
            lock (_gate)
            {
                return _lastDownloadSource;
            }
        }
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        List<(UpdateSourceDescriptor Descriptor, VelopackAssetFeed Feed)> successful = [];
        Exception? lastException = null;
        foreach (UpdateSourceDescriptor descriptor in _sources)
        {
            try
            {
                VelopackAssetFeed feed = await descriptor.Source.GetReleaseFeed(
                        logger,
                        appId,
                        channel,
                        stagingId,
                        latestLocalRelease)
                    .ConfigureAwait(false);
                successful.Add((descriptor, feed));
            }
            catch (Exception exception) when (exception is not
                OperationCanceledException and not UpdateSourceConflictException)
            {
                lastException = exception;
            }
        }

        if (successful.Count == 0)
        {
            throw new UpdateSourcesUnavailableException(lastException);
        }

        Dictionary<AssetKey, VelopackAsset> merged = [];
        Dictionary<AssetKey, List<AssetCandidate>> candidates = [];
        foreach ((UpdateSourceDescriptor descriptor, VelopackAssetFeed feed) in successful)
        {
            foreach (VelopackAsset asset in feed.Assets ?? [])
            {
                ValidateAsset(asset);
                AssetKey key = AssetKey.Create(asset);
                if (merged.TryGetValue(key, out VelopackAsset? existing))
                {
                    EnsureSameAsset(existing, asset);
                }
                else
                {
                    merged.Add(key, asset);
                }

                if (!candidates.TryGetValue(key, out List<AssetCandidate>? sourceCandidates))
                {
                    sourceCandidates = [];
                    candidates.Add(key, sourceCandidates);
                }

                sourceCandidates.Add(new AssetCandidate(descriptor, asset));
            }
        }

        lock (_gate)
        {
            _candidates = candidates.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<AssetCandidate>)pair.Value);
            _lastDownloadSource = null;
        }

        return new VelopackAssetFeed { Assets = [.. merged.Values] };
    }

    public async Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        AssetKey key = AssetKey.Create(releaseEntry);
        IReadOnlyList<AssetCandidate> candidates;
        lock (_gate)
        {
            if (!_candidates.TryGetValue(key, out candidates!))
            {
                throw new UpdateSourceConflictException(
                    "The selected update was not present in the verified source set.");
            }
        }

        Exception? lastException = null;
        foreach (AssetCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await candidate.Descriptor.Source.DownloadReleaseEntry(
                        logger,
                        candidate.Asset,
                        localFile,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    _lastDownloadSource = candidate.Descriptor.Name;
                }

                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastException = exception;
                TryDeletePartialFile(localFile);
            }
        }

        throw new UpdateSourcesUnavailableException(lastException);
    }

    internal string? GetSourceNames(VelopackAsset asset)
    {
        AssetKey key = AssetKey.Create(asset);
        lock (_gate)
        {
            return _candidates.TryGetValue(key, out IReadOnlyList<AssetCandidate>? candidates)
                ? string.Join(" / ", candidates.Select(static item => item.Descriptor.Name))
                : null;
        }
    }

    private static void ValidateAsset(VelopackAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.PackageId) ||
            asset.Version is null ||
            string.IsNullOrWhiteSpace(asset.FileName) ||
            !string.Equals(Path.GetFileName(asset.FileName), asset.FileName,
                StringComparison.Ordinal) ||
            asset.FileName.Contains('\\') ||
            asset.Size <= 0 ||
            !IsSha256(asset.SHA256))
        {
            throw new UpdateSourceConflictException("The signed update feed contains an invalid asset.");
        }
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureSameAsset(VelopackAsset left, VelopackAsset right)
    {
        if (!string.Equals(left.FileName, right.FileName, StringComparison.Ordinal) ||
            !string.Equals(left.SHA256, right.SHA256, StringComparison.OrdinalIgnoreCase) ||
            left.Size != right.Size)
        {
            throw new UpdateSourceConflictException(
                "Trusted update sources disagree about the same release.");
        }
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record AssetCandidate(
        UpdateSourceDescriptor Descriptor,
        VelopackAsset Asset);

    private readonly record struct AssetKey(
        string PackageId,
        string Version,
        VelopackAssetType Type)
    {
        public static AssetKey Create(VelopackAsset asset) => new(
            asset.PackageId ?? string.Empty,
            asset.Version?.ToFullString() ?? string.Empty,
            asset.Type);
    }
}
