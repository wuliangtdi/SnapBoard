using SnapBoard.Domain.Clipboard;

namespace SnapBoard.Domain.Tests;

public sealed class ClipboardItemIdTests
{
    [Fact]
    public void NewCreatesDistinctNonEmptyIdentifiers()
    {
        ClipboardItemId first = ClipboardItemId.New();
        ClipboardItemId second = ClipboardItemId.New();

        Assert.NotEqual(Guid.Empty, first.Value);
        Assert.NotEqual(first, second);
    }
}
