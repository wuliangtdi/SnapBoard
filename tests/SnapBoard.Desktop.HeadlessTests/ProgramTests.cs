using SnapBoard.Desktop.Bootstrap;
using SnapBoard.Platform.Abstractions.Desktop;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class ProgramTests
{
    [Fact]
    public void BackgroundSecondInstanceDoesNotActivateMainWindow()
    {
        SingleInstanceCommand command = Program.GetSingleInstanceCommand(["--background"]);

        Assert.Equal(SingleInstanceCommand.RemainInBackground, command);
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
