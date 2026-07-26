using Microsoft.Data.Sqlite;

namespace SnapBoard.Infrastructure.Tests;

public sealed class SqliteRuntimeTests
{
    [Fact]
    public void BundledSqliteContainsSecurityFixForCve20256965()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        string versionText = Assert.IsType<string>(command.ExecuteScalar());
        Version version = Version.Parse(versionText);

        Assert.True(
            version >= new Version(3, 50, 2),
            $"Expected SQLite 3.50.2 or newer, but found {versionText}.");
    }
}
