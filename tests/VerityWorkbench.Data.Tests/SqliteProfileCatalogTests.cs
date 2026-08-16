using System.Reflection;
using Microsoft.Data.Sqlite;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileCatalogTests
{
    [Fact]
    public async Task LocatorRoundTripsAfterCatalogIsReopened()
    {
        using var database = new CatalogTestDatabase();
        var addedAt = new DateTimeOffset(2026, 8, 15, 12, 30, 0, TimeSpan.FromHours(-7));
        var workspace = Path.Combine(database.DirectoryPath, "profiles", "subject-a", ".", "workload");
        var locator = new StoredProfileLocator(Guid.NewGuid(), workspace, addedAt);

        await new SqliteProfileCatalog(database.DatabasePath).AddAsync(locator);
        var loaded = await new SqliteProfileCatalog(database.DatabasePath).GetAllAsync();

        var actual = Assert.Single(loaded);
        Assert.Equal(locator.ProfileId, actual.ProfileId);
        Assert.Equal(Path.GetFullPath(workspace), actual.WorkspaceRoot);
        Assert.Equal(addedAt, actual.AddedAtUtc);
        Assert.Equal(ProfileLocatorState.Ready, actual.State);
    }

    [Fact]
    public async Task PendingLocatorCanBeMarkedReadyAndTransitionSurvivesReopen()
    {
        using var database = new CatalogTestDatabase();
        var locator = CreateLocator(database, Guid.NewGuid(), "pending");
        await new SqliteProfileCatalog(database.DatabasePath).AddPendingAsync(locator);

        var pending = Assert.Single(
            await new SqliteProfileCatalog(database.DatabasePath).GetAllAsync());
        Assert.Equal(ProfileLocatorState.Pending, pending.State);

        Assert.True(await new SqliteProfileCatalog(database.DatabasePath)
            .MarkReadyAsync(locator.ProfileId));

        var ready = Assert.Single(
            await new SqliteProfileCatalog(database.DatabasePath).GetAllAsync());
        Assert.Equal(ProfileLocatorState.Ready, ready.State);
    }

    [Fact]
    public async Task VersionOneLocatorMigratesAsReady()
    {
        using var database = new CatalogTestDatabase();
        var locator = CreateLocator(database, Guid.NewGuid(), "version-one");
        await CreateVersionOneCatalogAsync(database.DatabasePath, locator);

        var catalog = new SqliteProfileCatalog(database.DatabasePath);
        await catalog.InitializeAsync();

        Assert.Equal(2L, await ReadSchemaVersionAsync(database.DatabasePath));
        var migrated = Assert.Single(await catalog.GetAllAsync());
        Assert.Equal(locator.ProfileId, migrated.ProfileId);
        Assert.Equal(ProfileLocatorState.Ready, migrated.State);
    }

    [Fact]
    public async Task EveryCatalogConnectionEnablesSecureDelete()
    {
        using var database = new CatalogTestDatabase();
        var catalog = new SqliteProfileCatalog(database.DatabasePath);
        await catalog.InitializeAsync();

        await using var connection = await OpenCatalogConnectionAsync(catalog);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete;";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MalformedRowsAreSkippedWithoutHidingHealthyLocators()
    {
        using var database = new CatalogTestDatabase();
        var catalog = new SqliteProfileCatalog(database.DatabasePath);
        var valid = CreateLocator(database, Guid.NewGuid(), "valid");
        await catalog.AddAsync(valid);
        await InsertMalformedLocatorsAsync(database);

        var loaded = await catalog.GetAllAsync();

        Assert.Equal(valid.ProfileId, Assert.Single(loaded).ProfileId);
        Assert.Equal(2, catalog.LastInvalidLocatorCount);
    }

    [Fact]
    public async Task DuplicateProfileIdIsRejectedWithoutReplacingOriginal()
    {
        using var database = new CatalogTestDatabase();
        var catalog = new SqliteProfileCatalog(database.DatabasePath);
        var profileId = Guid.NewGuid();
        var original = CreateLocator(database, profileId, "original");
        await catalog.AddAsync(original);

        await Assert.ThrowsAsync<ProfileLocatorConflictException>(
            () => catalog.AddAsync(CreateLocator(database, profileId, "replacement")));

        var saved = Assert.Single(await catalog.GetAllAsync());
        Assert.Equal(Path.GetFullPath(original.WorkspaceRoot), saved.WorkspaceRoot);
    }

    [Fact]
    public async Task CaseInsensitiveDuplicateWorkspaceIsRejected()
    {
        using var database = new CatalogTestDatabase();
        var catalog = new SqliteProfileCatalog(database.DatabasePath);
        var original = CreateLocator(database, Guid.NewGuid(), "Case-Sensitive-Name");
        await catalog.AddAsync(original);

        var duplicate = new StoredProfileLocator(
            Guid.NewGuid(),
            original.WorkspaceRoot.ToUpperInvariant(),
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<ProfileLocatorConflictException>(
            () => catalog.AddAsync(duplicate));
        Assert.Equal(Path.GetFullPath(duplicate.WorkspaceRoot), exception.WorkspaceRoot);
        Assert.Single(await catalog.GetAllAsync());
    }

    [Theory]
    [InlineData("existing", "existing/child")]
    [InlineData("existing/child", "existing")]
    public async Task NestedWorkspaceIsRejected(string existingRelative, string candidateRelative)
    {
        using var database = new CatalogTestDatabase();
        var catalog = new SqliteProfileCatalog(database.DatabasePath);
        var existing = CreateLocator(database, Guid.NewGuid(), existingRelative);
        var candidate = CreateLocator(database, Guid.NewGuid(), candidateRelative);
        await catalog.AddAsync(existing);

        await Assert.ThrowsAsync<ProfileLocatorConflictException>(() => catalog.AddAsync(candidate));

        Assert.Single(await catalog.GetAllAsync());
    }

    [Fact]
    public async Task RemoveDeletesOnlyRequestedLocatorAndReportsMissingId()
    {
        using var database = new CatalogTestDatabase();
        var catalog = new SqliteProfileCatalog(database.DatabasePath);
        var first = CreateLocator(database, Guid.NewGuid(), "first");
        var second = CreateLocator(database, Guid.NewGuid(), "second");
        await catalog.AddAsync(first);
        await catalog.AddAsync(second);

        Assert.True(await catalog.RemoveAsync(first.ProfileId));
        Assert.False(await catalog.RemoveAsync(first.ProfileId));

        var remaining = Assert.Single(await catalog.GetAllAsync());
        Assert.Equal(second.ProfileId, remaining.ProfileId);
    }

    private static StoredProfileLocator CreateLocator(
        CatalogTestDatabase database,
        Guid profileId,
        string relativeWorkspace) =>
        new(
            profileId,
            Path.Combine(database.DirectoryPath, "workspaces", relativeWorkspace.Replace('/', Path.DirectorySeparatorChar)),
            new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero));

    private static async Task<SqliteConnection> OpenCatalogConnectionAsync(
        SqliteProfileCatalog catalog)
    {
        var method = typeof(SqliteProfileCatalog).GetMethod(
            "OpenConnectionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var invocation = method.Invoke(catalog, [CancellationToken.None]);
        var connectionTask = Assert.IsType<Task<SqliteConnection>>(invocation);
        return await connectionTask;
    }

    private static async Task CreateVersionOneCatalogAsync(
        string databasePath,
        StoredProfileLocator locator)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenRawConnectionAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE profile_locators (
                profile_id TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                workspace_root TEXT NOT NULL COLLATE NOCASE UNIQUE,
                added_utc TEXT NOT NULL
            );

            INSERT INTO profile_locators (profile_id, workspace_root, added_utc)
            VALUES ($profileId, $workspaceRoot, $addedUtc);

            PRAGMA user_version = 1;
            """;
        command.Parameters.AddWithValue("$profileId", locator.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$workspaceRoot", Path.GetFullPath(locator.WorkspaceRoot));
        command.Parameters.AddWithValue("$addedUtc", locator.AddedAtUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = await OpenRawConnectionAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertMalformedLocatorsAsync(CatalogTestDatabase database)
    {
        await using var connection = await OpenRawConnectionAsync(database.DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profile_locators (profile_id, workspace_root, added_utc, state)
            VALUES ('not-a-guid', $badGuidWorkspace, '2026-08-15T20:00:00.0000000+00:00', 'Ready');

            INSERT INTO profile_locators (profile_id, workspace_root, added_utc, state)
            VALUES ($badTimeId, $badTimeWorkspace, 'not-a-timestamp', 'Ready');
            """;
        command.Parameters.AddWithValue(
            "$badGuidWorkspace",
            Path.Combine(database.DirectoryPath, "workspaces", "bad-guid"));
        command.Parameters.AddWithValue("$badTimeId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue(
            "$badTimeWorkspace",
            Path.Combine(database.DirectoryPath, "workspaces", "bad-time"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenRawConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed class CatalogTestDatabase : IDisposable
    {
        public CatalogTestDatabase()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "VerityWorkbench.Data.Tests",
                "ProfileCatalog",
                Guid.NewGuid().ToString("N"));
            DatabasePath = Path.Combine(DirectoryPath, "catalog.db");
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
