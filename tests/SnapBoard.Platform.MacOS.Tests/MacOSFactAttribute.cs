namespace SnapBoard.Platform.MacOS.Tests;

internal sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "This integration test requires macOS.";
        }
    }
}
