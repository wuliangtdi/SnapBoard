using System.Runtime.Versioning;
using SnapBoard.Infrastructure.Storage;
using SnapBoard.Platform.Abstractions.Storage;
using SnapBoard.Platform.MacOS.Storage;
using SnapBoard.Platform.Windows.Storage;

namespace SnapBoard.StorageMigrator;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? manifestPath = ParseManifestPath(args);
        if (manifestPath is null)
        {
            return 4;
        }

        if (OperatingSystem.IsWindows())
        {
            return RunWindowsAsync(manifestPath).GetAwaiter().GetResult();
        }

        if (OperatingSystem.IsMacOS())
        {
            return RunMacOSAsync(manifestPath).GetAwaiter().GetResult();
        }

        return 3;
    }

    [SupportedOSPlatform("windows")]
    private static Task<int> RunWindowsAsync(string manifestPath) =>
        RunAsync(manifestPath, new WindowsStoragePlatformService());

    [SupportedOSPlatform("macos")]
    private static Task<int> RunMacOSAsync(string manifestPath) =>
        RunAsync(manifestPath, new MacOSStoragePlatformService());

    private static async Task<int> RunAsync(
        string manifestPath,
        IStoragePlatformService platform)
    {
        try
        {
            StorageMigrationExecutor executor = new(platform);
            StorageMigrationExecutionResult result = await executor.ExecuteAsync(
                manifestPath,
                CancellationToken.None);
            return result.Status == StorageMigrationExecutionStatus.Completed ? 0 : 2;
        }
        catch (StorageMetadataException)
        {
            return 5;
        }
        catch (UnauthorizedAccessException)
        {
            return 6;
        }
        catch (IOException)
        {
            return 7;
        }
        catch (InvalidOperationException)
        {
            return 8;
        }
    }

    private static string? ParseManifestPath(string[] args)
    {
        if (args.Length != 2 ||
            !string.Equals(args[0], "--manifest", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(args[1]))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(args[1]);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
