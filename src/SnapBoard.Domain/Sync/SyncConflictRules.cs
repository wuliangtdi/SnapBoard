using System.Text;

namespace SnapBoard.Domain.Sync;

public readonly record struct SyncLogicalVersion : IComparable<SyncLogicalVersion>
{
    public SyncLogicalVersion(long logicalTime, Guid deviceId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalTime);
        ArgumentOutOfRangeException.ThrowIfEqual(deviceId, Guid.Empty);
        LogicalTime = logicalTime;
        DeviceId = deviceId;
    }

    public long LogicalTime { get; }

    public Guid DeviceId { get; }

    public int CompareTo(SyncLogicalVersion other)
    {
        int timeComparison = LogicalTime.CompareTo(other.LogicalTime);
        return timeComparison != 0
            ? timeComparison
            : string.Compare(
                DeviceId.ToString("N"),
                other.DeviceId.ToString("N"),
                StringComparison.Ordinal);
    }

    public bool IsNewerThan(SyncLogicalVersion? current) =>
        current is null || CompareTo(current.Value) > 0;

    public static bool operator <(SyncLogicalVersion left, SyncLogicalVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(SyncLogicalVersion left, SyncLogicalVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(SyncLogicalVersion left, SyncLogicalVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(SyncLogicalVersion left, SyncLogicalVersion right) =>
        left.CompareTo(right) >= 0;
}

public enum SyncMutationKind
{
    Content = 1,
    Tags = 2,
    Pin = 3,
    Delete = 4,
    Restore = 5,
}

public sealed record SyncItemConflictState(
    bool IsDeleted,
    SyncLogicalVersion? ContentVersion = null,
    SyncLogicalVersion? TagsVersion = null,
    SyncLogicalVersion? PinVersion = null,
    SyncLogicalVersion? DeletionVersion = null);

public static class SyncConflictRules
{
    public static bool ShouldApply(
        SyncItemConflictState state,
        SyncMutationKind mutation,
        SyncLogicalVersion incoming)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (mutation is SyncMutationKind.Delete or SyncMutationKind.Restore)
        {
            return incoming.IsNewerThan(state.DeletionVersion) &&
                (mutation == SyncMutationKind.Delete || state.IsDeleted);
        }

        if (state.IsDeleted)
        {
            return false;
        }

        SyncLogicalVersion? current = mutation switch
        {
            SyncMutationKind.Content => state.ContentVersion,
            SyncMutationKind.Tags => state.TagsVersion,
            SyncMutationKind.Pin => state.PinVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        return incoming.IsNewerThan(current);
    }

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        Dictionary<string, string> unique = new(StringComparer.Ordinal);
        foreach (string tag in tags)
        {
            if (tag is null)
            {
                throw new ArgumentException("A sync tag cannot be null.", nameof(tags));
            }

            string value = tag.Trim().Normalize(NormalizationForm.FormC);
            if (value.Length is 0 or > 64 || value.Any(char.IsControl))
            {
                throw new ArgumentException("A sync tag is invalid.", nameof(tags));
            }

            string normalized = value.ToUpperInvariant();
            if (!unique.TryGetValue(normalized, out string? existing) ||
                string.Compare(value, existing, StringComparison.Ordinal) < 0)
            {
                unique[normalized] = value;
            }
        }

        if (unique.Count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(tags), "At most 32 tags can be assigned.");
        }

        return unique
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToArray();
    }
}
