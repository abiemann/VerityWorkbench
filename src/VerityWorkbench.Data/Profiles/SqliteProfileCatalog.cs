using System.Globalization;
using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Data.Profiles;

public sealed class SqliteProfileCatalog
{
    private const int SchemaVersion = 2;

    private const string CreateVersion1SchemaSql = """
        CREATE TABLE IF NOT EXISTS profile_locators (
            profile_id TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            workspace_root TEXT NOT NULL COLLATE NOCASE UNIQUE,
            added_utc TEXT NOT NULL
        );

        PRAGMA user_version = 1;
        """;

    private const string MigrateVersion1ToVersion2Sql = """
        ALTER TABLE profile_locators
            ADD COLUMN state TEXT NOT NULL DEFAULT 'Ready'
            CHECK (state IN ('Pending', 'Ready'));

        PRAGMA user_version = 2;
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;
    private int _lastInvalidLocatorCount;

    public SqliteProfileCatalog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public int LastInvalidLocatorCount => Volatile.Read(ref _lastInvalidLocatorCount);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var parentDirectory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = await ReadSchemaVersionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (version > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The profile catalog schema version {version} is newer than supported version {SchemaVersion}.");
            }

            if (version < 1)
            {
                await ApplyMigrationStepAsync(
                        connection,
                        transaction,
                        CreateVersion1SchemaSql,
                        cancellationToken)
                    .ConfigureAwait(false);
                version = 1;
            }

            if (version < 2)
            {
                await ApplyMigrationStepAsync(
                        connection,
                        transaction,
                        MigrateVersion1ToVersion2Sql,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task AddAsync(
        StoredProfileLocator locator,
        CancellationToken cancellationToken = default) =>
        await AddWithStateAsync(locator, ProfileLocatorState.Ready, cancellationToken).ConfigureAwait(false);

    public async Task AddPendingAsync(
        StoredProfileLocator locator,
        CancellationToken cancellationToken = default) =>
        await AddWithStateAsync(locator, ProfileLocatorState.Pending, cancellationToken).ConfigureAwait(false);

    private async Task AddWithStateAsync(
        StoredProfileLocator locator,
        ProfileLocatorState state,
        CancellationToken cancellationToken)
    {
        var normalizedLocator = Normalize(locator with { State = state });
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        if (await ConflictsAsync(connection, transaction, normalizedLocator, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ProfileLocatorConflictException(normalizedLocator.WorkspaceRoot);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO profile_locators (profile_id, workspace_root, added_utc, state)
                VALUES ($profileId, $workspaceRoot, $addedUtc, $state);
                """;
            command.Parameters.AddWithValue("$profileId", normalizedLocator.ProfileId.ToString("D"));
            command.Parameters.AddWithValue("$workspaceRoot", normalizedLocator.WorkspaceRoot);
            command.Parameters.AddWithValue("$addedUtc", FormatTimestamp(normalizedLocator.AddedAtUtc));
            command.Parameters.AddWithValue("$state", normalizedLocator.State.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ProfileLocatorConflictException(normalizedLocator.WorkspaceRoot, exception);
        }
    }

    public async Task<bool> MarkReadyAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile ID is required.", nameof(profileId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE profile_locators
            SET state = 'Ready'
            WHERE profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affectedRows != 0;
    }

    public async Task<bool> RemoveAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile ID is required.", nameof(profileId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM profile_locators WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affectedRows != 0;
    }

    public async Task<IReadOnlyList<StoredProfileLocator>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, workspace_root, added_utc, state
            FROM profile_locators
            ORDER BY added_utc, profile_id;
            """;

        var locators = new List<StoredProfileLocator>();
        var invalidLocatorCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(0), out var profileId)
                || !TryParseTimestamp(reader.GetString(2), out var addedAtUtc)
                || !Enum.TryParse<ProfileLocatorState>(
                    reader.GetString(3),
                    ignoreCase: false,
                    out var state)
                || !Enum.IsDefined(state))
            {
                invalidLocatorCount++;
                continue;
            }

            locators.Add(new StoredProfileLocator(
                profileId,
                reader.GetString(1),
                addedAtUtc,
                state));
        }

        Volatile.Write(ref _lastInvalidLocatorCount, invalidLocatorCount);
        return locators;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA secure_delete = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task ApplyMigrationStepAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string migrationSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = migrationSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ConflictsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfileLocator candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT profile_id, workspace_root FROM profile_locators;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(
                    reader.GetString(0),
                    candidate.ProfileId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase)
                || PathsOverlap(reader.GetString(1), candidate.WorkspaceRoot))
            {
                return true;
            }
        }

        return false;
    }

    private static StoredProfileLocator Normalize(StoredProfileLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        if (locator.ProfileId == Guid.Empty)
        {
            throw new ArgumentException("A profile ID is required.", nameof(locator));
        }

        if (!Enum.IsDefined(locator.State))
        {
            throw new ArgumentOutOfRangeException(
                nameof(locator),
                locator.State,
                "The profile locator state is unsupported.");
        }

        var workspaceValidation = WorkspacePathPolicy.Validate(locator.WorkspaceRoot);
        if (!workspaceValidation.IsValid)
        {
            throw new ArgumentException(workspaceValidation.Error, nameof(locator));
        }

        return locator with
        {
            WorkspaceRoot = workspaceValidation.NormalizedPath!,
            AddedAtUtc = locator.AddedAtUtc.ToUniversalTime(),
        };
    }

    private static bool PathsOverlap(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        if (string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstWithSeparator = normalizedFirst + Path.DirectorySeparatorChar;
        var secondWithSeparator = normalizedSecond + Path.DirectorySeparatorChar;
        return firstWithSeparator.StartsWith(secondWithSeparator, StringComparison.OrdinalIgnoreCase)
            || secondWithSeparator.StartsWith(firstWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryParseTimestamp(string timestamp, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(
            timestamp,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out result);
}
