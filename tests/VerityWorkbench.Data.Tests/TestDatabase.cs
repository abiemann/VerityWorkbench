using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

internal sealed class TestDatabase : IDisposable
{
    private readonly string _directoryPath;

    public TestDatabase()
    {
        _directoryPath = Path.Combine(
            Path.GetTempPath(),
            "VerityWorkbench.Data.Tests",
            Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(_directoryPath, "profiles.db");
        Store = new SqliteProfileStore(DatabasePath);
    }

    public string DatabasePath { get; }

    public SqliteProfileStore Store { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
