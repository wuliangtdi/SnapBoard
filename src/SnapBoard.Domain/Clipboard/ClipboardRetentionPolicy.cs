namespace SnapBoard.Domain.Clipboard;

/// <summary>
/// 本地历史的容量与过期边界。置顶记录由调用方显式豁免，不计入自动淘汰候选。
/// </summary>
public sealed record ClipboardRetentionPolicy
{
    public const int DefaultMaximumItemCount = 10_000;
    public const long DefaultMaximumStorageBytes = 1024L * 1024 * 1024;

    public ClipboardRetentionPolicy(
        int maximumItemCount,
        TimeSpan maximumAge,
        long maximumStorageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItemCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStorageBytes);
        MaximumItemCount = maximumItemCount;
        MaximumAge = maximumAge;
        MaximumStorageBytes = maximumStorageBytes;
    }

    public int MaximumItemCount { get; }

    public TimeSpan MaximumAge { get; }

    public long MaximumStorageBytes { get; }

    public static ClipboardRetentionPolicy Default { get; } = new(
        DefaultMaximumItemCount,
        TimeSpan.FromDays(30),
        DefaultMaximumStorageBytes);
}
