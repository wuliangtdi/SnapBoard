using SnapBoard.Platform.Windows.Desktop;

namespace SnapBoard.Desktop.HeadlessTests;

public sealed class ProgramTests
{
    [Fact]
    public void BackgroundSecondInstanceDoesNotActivateMainWindow()
    {
        SingleInstanceCommand command = Program.GetSingleInstanceCommand(["--background"]);

        Assert.Equal(SingleInstanceCommand.RemainInBackground, command);
    }
}
