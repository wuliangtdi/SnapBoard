namespace SnapBoard.Infrastructure.Storage;

public sealed record StorageBootstrapPaths(
    string ApplicationDataDirectory,
    string BootstrapDirectory,
    string DefaultDataRoot,
    string LegacyDataRoot,
    string LocationPath,
    string MigrationStatePath,
    string MigrationLockPath)
{
    public static StorageBootstrapPaths CreateDefault()
    {
        string specialFolder = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(specialFolder))
        {
            throw new InvalidOperationException("The application data directory is unavailable.");
        }

        return Create(Path.Combine(specialFolder, "SnapBoard"));
    }

    public static StorageBootstrapPaths Create(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        string applicationData = Path.GetFullPath(applicationDataDirectory);
        string bootstrap = Path.Combine(applicationData, "bootstrap");
        return new StorageBootstrapPaths(
            applicationData,
            bootstrap,
            Path.Combine(applicationData, "data"),
            applicationData,
            Path.Combine(bootstrap, "storage-location.json"),
            Path.Combine(bootstrap, "migration-state.json"),
            Path.Combine(bootstrap, "migration.lock"));
    }

    public string GetManifestPath(string migrationId) =>
        Path.Combine(BootstrapDirectory, $"migration-{migrationId}.json");

    public string GetStartupAcknowledgementPath(string migrationId) =>
        Path.Combine(BootstrapDirectory, $"startup-ack-{migrationId}.json");
}
