using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class ProgramTests
{
    [Fact]
    public void StorageStartupOptionsRequireSingleNonEmptyValues()
    {
        string? root = Program.GetOptionValue(
            ["--storage-bootstrap-root", @"C:\isolated", "--migration-id", "m-1234567890123456"],
            "--storage-bootstrap-root");
        string? migration = Program.GetOptionValue(
            ["--storage-bootstrap-root", @"C:\isolated", "--migration-id", "m-1234567890123456"],
            "--migration-id");

        Assert.Equal(@"C:\isolated", root);
        Assert.Equal("m-1234567890123456", migration);
        Assert.Throws<ArgumentException>(() => Program.GetOptionValue(
            ["--migration-id", "first", "--migration-id", "second"],
            "--migration-id"));
        Assert.Throws<ArgumentException>(() => Program.GetOptionValue(
            ["--storage-bootstrap-root", "--quick"],
            "--storage-bootstrap-root"));
    }

    [Fact]
    public void BackgroundSecondInstanceDoesNotActivateMainWindow()
    {
        SingleInstanceCommand command = Program.GetSingleInstanceCommand(["--background"]);

        Assert.Equal(SingleInstanceCommand.RemainInBackground, command);
    }

    [Fact]
    public void QuickArgumentAlwaysRequestsTheQuickWindow()
    {
        SingleInstanceCommand command = Program.GetSingleInstanceCommand(["--quick"]);

        Assert.Equal(SingleInstanceCommand.ShowQuickWindow, command);
    }

    [Fact]
    public void LoginItemLaunchDefaultsToBackgroundButExplicitWindowWins()
    {
        Assert.Equal(
            DesktopStartupMode.Background,
            Program.GetStartupMode([], launchedAsLoginItem: true));
        Assert.Equal(
            DesktopStartupMode.QuickWindow,
            Program.GetStartupMode(["--quick"], launchedAsLoginItem: true));
    }
}
