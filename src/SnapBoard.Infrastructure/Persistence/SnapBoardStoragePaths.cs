namespace SnapBoard.Infrastructure.Persistence;

public sealed record SnapBoardStoragePaths(
    string RootDirectory,
    string DatabasePath,
    string BlobDirectory,
    string RecoveryDirectory)
{
    public static SnapBoardStoragePaths CreateDefault()
    {
        string specialFolder = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string root = Path.Combine(
            string.IsNullOrWhiteSpace(specialFolder) ? AppContext.BaseDirectory : specialFolder,
            "SnapBoard");
        return Create(root);
    }

    public static SnapBoardStoragePaths Create(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        return new SnapBoardStoragePaths(
            root,
            Path.Combine(root, "snapboard.db"),
            Path.Combine(root, "blobs"),
            Path.Combine(root, "recovery"));
    }
}
