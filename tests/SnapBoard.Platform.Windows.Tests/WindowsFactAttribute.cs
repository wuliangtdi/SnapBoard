namespace SnapBoard.Platform.Windows.Tests;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires a Windows desktop session.";
        }
    }
}
