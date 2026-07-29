using Velopack;

namespace SnapBoard.Update.Velopack;

public static class VelopackBootstrap
{
    public static void Run(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        VelopackApp.Build()
            .SetArgs([.. arguments])
            .SetAutoApplyOnStartup(false)
            .SetAppUserModelId("com.wuliangtdi.snapboard")
            .Run();
    }
}
