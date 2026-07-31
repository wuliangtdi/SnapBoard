namespace SnapBoard.Domain.Clipboard;

/// <summary>
/// 本地历史的容量与过期边界。
/// </summary>
public sealed record ClipboardRetentionPolicy
{
    public const int DefaultMaximumItemCount = 10_000;
    public const long DefaultMaximumStorageBytes = 1024L * 1024 * 1024;

    public ClipboardRetentionPolicy(
        int maximumItemCount,
        TimeSpan maximumAge,
        long maximumStorageBytes,
        bool preservePinnedItems = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItemCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStorageBytes);
        MaximumItemCount = maximumItemCount;
        MaximumAge = maximumAge;
        MaximumStorageBytes = maximumStorageBytes;
        PreservePinnedItems = preservePinnedItems;
    }

    public int MaximumItemCount { get; }

    public TimeSpan MaximumAge { get; }

    public long MaximumStorageBytes { get; }

    public bool PreservePinnedItems { get; }

    public static ClipboardRetentionPolicy Default { get; } = new(
        DefaultMaximumItemCount,
        TimeSpan.FromDays(30),
        DefaultMaximumStorageBytes);
}
