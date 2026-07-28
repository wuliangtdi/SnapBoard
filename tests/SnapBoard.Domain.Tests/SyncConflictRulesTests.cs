using SnapBoard.Domain.Sync;

namespace SnapBoard.Domain.Tests;

public sealed class SyncConflictRulesTests
{
    private static readonly Guid DeviceA =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DeviceB =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void EqualLogicalTimeUsesCanonicalDeviceIdentity()
    {
        SyncLogicalVersion current = new(7, DeviceA);
        SyncLogicalVersion incoming = new(7, DeviceB);

        Assert.True(incoming.IsNewerThan(current));
        Assert.False(current.IsNewerThan(incoming));
    }

    [Fact]
    public void TombstoneRejectsModificationUntilExplicitNewerRestore()
    {
        SyncItemConflictState deleted = new(
            IsDeleted: true,
            ContentVersion: new SyncLogicalVersion(20, DeviceB),
            DeletionVersion: new SyncLogicalVersion(10, DeviceA));

        Assert.False(SyncConflictRules.ShouldApply(
            deleted,
            SyncMutationKind.Content,
            new SyncLogicalVersion(100, DeviceB)));
        Assert.False(SyncConflictRules.ShouldApply(
            deleted,
            SyncMutationKind.Restore,
            new SyncLogicalVersion(9, DeviceB)));
        Assert.True(SyncConflictRules.ShouldApply(
            deleted,
            SyncMutationKind.Restore,
            new SyncLogicalVersion(11, DeviceB)));
    }

    [Fact]
    public void TagSetIsNormalizedDeduplicatedAndStable()
    {
        IReadOnlyList<string> tags = SyncConflictRules.NormalizeTags(
            [" beta ", "Alpha", "alpha", "e\u0301", "é"]);

        Assert.Equal(["Alpha", "beta", "é"], tags);
    }
}
