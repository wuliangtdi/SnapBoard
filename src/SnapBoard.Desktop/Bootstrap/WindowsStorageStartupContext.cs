using System.Runtime.Versioning;
using SnapBoard.Infrastructure.Storage;
using SnapBoard.Platform.Abstractions.Storage;
using SnapBoard.Platform.Windows.Storage;

namespace SnapBoard.Desktop.Bootstrap;

internal sealed class WindowsStorageStartupContext : IDisposable
{
    private int _disposed;

    private WindowsStorageStartupContext(
        string? migrationId,
        IStoragePlatformService platformService,
        StorageBootstrapPaths bootstrapPaths,
        StorageLocationStore locationStore,
        ResolvedStorageLocation activeLocation,
        StorageManagementService managementService)
    {
        MigrationId = migrationId;
        PlatformService = platformService;
        BootstrapPaths = bootstrapPaths;
        LocationStore = locationStore;
        ActiveLocation = activeLocation;
        ManagementService = managementService;
    }

    public string? MigrationId { get; }

    public IStoragePlatformService PlatformService { get; }

    public StorageBootstrapPaths BootstrapPaths { get; }

    public StorageLocationStore LocationStore { get; }

    public ResolvedStorageLocation ActiveLocation { get; }

    public StorageManagementService ManagementService { get; }

    [SupportedOSPlatform("windows")]
    public static WindowsStorageStartupContext Create(
        string? applicationDataDirectory,
        string? migrationId)
    {
        StorageBootstrapPaths bootstrapPaths = string.IsNullOrWhiteSpace(applicationDataDirectory)
            ? StorageBootstrapPaths.CreateDefault()
            : StorageBootstrapPaths.Create(applicationDataDirectory);
        WindowsStoragePlatformService platformService = new();
        StorageLocationStore locationStore = new(bootstrapPaths, platformService);
        try
        {
            ResolvedStorageLocation activeLocation = locationStore.ResolveOrCreateAsync(
                    migrationId,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            StorageManagementService managementService = new(
                bootstrapPaths,
                locationStore,
                activeLocation,
                platformService);
            return new WindowsStorageStartupContext(
                migrationId,
                platformService,
                bootstrapPaths,
                locationStore,
                activeLocation,
                managementService);
        }
        catch
        {
            locationStore.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            LocationStore.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
