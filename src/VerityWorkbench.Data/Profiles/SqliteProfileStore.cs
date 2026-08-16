using System.Globalization;
using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Profiles;

namespace VerityWorkbench.Data.Profiles;

public sealed class SqliteProfileStore
{
    private const int SchemaVersion = 2;

    private const string CreateVersion1SchemaSql = """
        CREATE TABLE IF NOT EXISTS profiles (
            id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            workspace_root TEXT NOT NULL,
            download_staging_root TEXT NULL,
            readiness TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS training_videos (
            id TEXT NOT NULL PRIMARY KEY,
            profile_id TEXT NOT NULL,
            file_path TEXT NOT NULL,
            recording_date_label TEXT NOT NULL,
            training_condition TEXT NOT NULL,
            is_archived INTEGER NOT NULL CHECK (is_archived IN (0, 1)),
            sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
            FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS ix_training_videos_profile_order
            ON training_videos(profile_id, sort_order, id);

        PRAGMA user_version = 1;
        """;

    private const string MigrateVersion1ToVersion2Sql = """
        CREATE UNIQUE INDEX IF NOT EXISTS ux_profiles_workspace_root_nocase
            ON profiles(workspace_root COLLATE NOCASE);

        PRAGMA user_version = 2;
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public SqliteProfileStore(string databasePath, bool createIfMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

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

            await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            using var transaction = connection.BeginTransaction(deferred: false);
            var version = await ReadSchemaVersionAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (version > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The profile database schema version {version} is newer than supported version {SchemaVersion}.");
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
        StoredProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();

        try
        {
            await EnsureIdentityAndLocationsAllowedAsync(
                    connection,
                    transaction,
                    profile,
                    isUpdate: false,
                    cancellationToken)
                .ConfigureAwait(false);
            await InsertProfileAsync(connection, transaction, profile, cancellationToken)
                .ConfigureAwait(false);
            await InsertTrainingVideosAsync(connection, transaction, profile, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsDisplayNameConflict(exception))
        {
            throw new ProfileNameConflictException(profile.DisplayName, exception);
        }
        catch (SqliteException exception) when (IsWorkspaceRootConflict(exception))
        {
            throw new ProfileWorkspaceConflictException(profile.WorkspaceRoot, exception);
        }
    }

    public async Task UpdateAsync(
        StoredProfile profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            await EnsureIdentityAndLocationsAllowedAsync(
                    connection,
                    transaction,
                    profile,
                    isUpdate: true,
                    cancellationToken)
                .ConfigureAwait(false);
            var affectedRows = await UpdateProfileRowAsync(
                    connection,
                    transaction,
                    profile,
                    expectedUpdatedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            if (affectedRows == 0)
            {
                throw new ProfileConcurrencyConflictException(profile.Id, expectedUpdatedAtUtc);
            }

            await DeleteTrainingVideosAsync(connection, transaction, profile.Id, cancellationToken)
                .ConfigureAwait(false);
            await InsertTrainingVideosAsync(connection, transaction, profile, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsDisplayNameConflict(exception))
        {
            throw new ProfileNameConflictException(profile.DisplayName, exception);
        }
        catch (SqliteException exception) when (IsWorkspaceRootConflict(exception))
        {
            throw new ProfileWorkspaceConflictException(profile.WorkspaceRoot, exception);
        }
    }

    public async Task<StoredProfile?> GetByIdAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("A profile ID is required.", nameof(profileId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var profile = await ReadProfileAsync(connection, profileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        var videos = await ReadTrainingVideosAsync(connection, profile.Id, cancellationToken)
            .ConfigureAwait(false);
        return profile with { TrainingVideos = videos };
    }

    public async Task<IReadOnlyList<StoredProfile>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConfiguredConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var profiles = await ReadProfilesAsync(connection, cancellationToken).ConfigureAwait(false);
        var results = new List<StoredProfile>(profiles.Count);

        foreach (var profile in profiles)
        {
            var videos = await ReadTrainingVideosAsync(connection, profile.Id, cancellationToken)
                .ConfigureAwait(false);
            results.Add(profile with { TrainingVideos = videos });
        }

        return results;
    }

    private async Task<SqliteConnection> OpenConfiguredConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task InsertProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO profiles (
                id,
                display_name,
                workspace_root,
                download_staging_root,
                readiness,
                created_utc,
                updated_utc)
            VALUES (
                $id,
                $displayName,
                $workspaceRoot,
                $downloadStagingRoot,
                $readiness,
                $createdUtc,
                $updatedUtc);
            """;
        AddProfileParameters(command, profile, includeCreatedTimestamp: true);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateProfileRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE profiles
            SET display_name = $displayName,
                readiness = $readiness,
                updated_utc = $updatedUtc
            WHERE id = $id
              AND updated_utc = $expectedUpdatedUtc;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$displayName", profile.DisplayName);
        command.Parameters.AddWithValue("$readiness", profile.Readiness);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$expectedUpdatedUtc", FormatTimestamp(expectedUpdatedAtUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddProfileParameters(
        SqliteCommand command,
        StoredProfile profile,
        bool includeCreatedTimestamp)
    {
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$displayName", profile.DisplayName);
        command.Parameters.AddWithValue("$workspaceRoot", profile.WorkspaceRoot);
        command.Parameters.AddWithValue(
            "$downloadStagingRoot",
            (object?)profile.DownloadStagingRoot ?? DBNull.Value);
        command.Parameters.AddWithValue("$readiness", profile.Readiness);
        command.Parameters.AddWithValue("$updatedUtc", FormatTimestamp(profile.UpdatedAtUtc));

        if (includeCreatedTimestamp)
        {
            command.Parameters.AddWithValue("$createdUtc", FormatTimestamp(profile.CreatedAtUtc));
        }
    }

    private static async Task DeleteTrainingVideosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM training_videos WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureIdentityAndLocationsAllowedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, display_name, workspace_root, download_staging_root FROM profiles;";

        var targetFound = false;
        string? storedWorkspaceRoot = null;
        string? storedDownloadStagingRoot = null;
        var nameConflict = false;
        var workspaceConflict = false;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var existingId = Guid.Parse(reader.GetString(0));
            if (isUpdate && existingId == profile.Id)
            {
                targetFound = true;
                storedWorkspaceRoot = reader.GetString(2);
                storedDownloadStagingRoot = reader.IsDBNull(3) ? null : reader.GetString(3);
                continue;
            }

            nameConflict |= string.Equals(
                reader.GetString(1),
                profile.DisplayName,
                StringComparison.OrdinalIgnoreCase);
            workspaceConflict |= string.Equals(
                reader.GetString(2),
                profile.WorkspaceRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        if (isUpdate)
        {
            if (!targetFound)
            {
                throw new KeyNotFoundException($"Profile '{profile.Id}' was not found.");
            }

            if (!string.Equals(
                    storedWorkspaceRoot,
                    profile.WorkspaceRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    storedDownloadStagingRoot,
                    profile.DownloadStagingRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A saved profile's workspace and download locations cannot be changed.");
            }
        }

        if (nameConflict)
        {
            throw new ProfileNameConflictException(
                profile.DisplayName,
                new InvalidOperationException("The display-name conflict was detected before writing."));
        }

        if (workspaceConflict)
        {
            throw new ProfileWorkspaceConflictException(
                profile.WorkspaceRoot,
                new InvalidOperationException("The workspace conflict was detected before writing."));
        }
    }

    private static async Task InsertTrainingVideosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProfile profile,
        CancellationToken cancellationToken)
    {
        foreach (var video in profile.TrainingVideos)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO training_videos (
                    id,
                    profile_id,
                    file_path,
                    recording_date_label,
                    training_condition,
                    is_archived,
                    sort_order)
                VALUES (
                    $id,
                    $profileId,
                    $filePath,
                    $recordingDateLabel,
                    $trainingCondition,
                    $isArchived,
                    $sortOrder);
                """;
            command.Parameters.AddWithValue("$id", video.Id.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$filePath", video.FilePath);
            command.Parameters.AddWithValue("$recordingDateLabel", video.RecordingDateLabel);
            command.Parameters.AddWithValue("$trainingCondition", video.Condition.ToString());
            command.Parameters.AddWithValue("$isArchived", video.IsArchived ? 1 : 0);
            command.Parameters.AddWithValue("$sortOrder", video.SortOrder);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<StoredProfile?> ReadProfileAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                display_name,
                workspace_root,
                download_staging_root,
                readiness,
                created_utc,
                updated_utc
            FROM profiles
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapProfile(reader)
            : null;
    }

    private static async Task<List<StoredProfile>> ReadProfilesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                display_name,
                workspace_root,
                download_staging_root,
                readiness,
                created_utc,
                updated_utc
            FROM profiles
            ORDER BY display_name COLLATE NOCASE, id;
            """;

        var profiles = new List<StoredProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(MapProfile(reader));
        }

        return profiles;
    }

    private static StoredProfile MapProfile(SqliteDataReader reader)
    {
        return new StoredProfile(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            []);
    }

    private static async Task<IReadOnlyList<StoredTrainingVideo>> ReadTrainingVideosAsync(
        SqliteConnection connection,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                file_path,
                recording_date_label,
                training_condition,
                is_archived,
                sort_order
            FROM training_videos
            WHERE profile_id = $profileId
            ORDER BY sort_order, id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));

        var videos = new List<StoredTrainingVideo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var conditionText = reader.GetString(3);
            if (!Enum.TryParse<TrainingCondition>(conditionText, ignoreCase: false, out var condition) ||
                !Enum.IsDefined(condition))
            {
                throw new InvalidDataException(
                    $"Training video '{reader.GetString(0)}' has unsupported condition '{conditionText}'.");
            }

            var storedPath = reader.GetString(1);
            string fullPath;
            try
            {
                if (string.IsNullOrWhiteSpace(storedPath) || !Path.IsPathFullyQualified(storedPath))
                {
                    throw new ArgumentException("The path is not absolute.", nameof(storedPath));
                }

                fullPath = Path.GetFullPath(storedPath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException(
                    $"Training video '{reader.GetString(0)}' has an invalid stored path.",
                    exception);
            }

            videos.Add(new StoredTrainingVideo(
                Guid.Parse(reader.GetString(0)),
                fullPath,
                reader.GetString(2),
                condition,
                reader.GetInt64(4) != 0,
                reader.GetInt32(5)));
        }

        return videos;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseTimestamp(string timestamp)
    {
        return DateTimeOffset.ParseExact(
            timestamp,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    private static bool IsDisplayNameConflict(SqliteException exception)
    {
        const int constraintError = 19;
        const int uniqueConstraint = 2067;

        return exception.SqliteErrorCode == constraintError &&
               exception.SqliteExtendedErrorCode == uniqueConstraint &&
               exception.Message.Contains("profiles.display_name", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkspaceRootConflict(SqliteException exception)
    {
        const int constraintError = 19;
        const int uniqueConstraint = 2067;

        return exception.SqliteErrorCode == constraintError &&
               exception.SqliteExtendedErrorCode == uniqueConstraint &&
               exception.Message.Contains("profiles.workspace_root", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateProfile(StoredProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Id == Guid.Empty)
        {
            throw new ArgumentException("A profile ID is required.", nameof(profile));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.WorkspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Readiness);
        ArgumentNullException.ThrowIfNull(profile.TrainingVideos);

        var videoIds = new HashSet<Guid>();
        foreach (var video in profile.TrainingVideos)
        {
            if (video.Id == Guid.Empty)
            {
                throw new ArgumentException("Every training video requires an ID.", nameof(profile));
            }

            if (!videoIds.Add(video.Id))
            {
                throw new ArgumentException(
                    $"Training video ID '{video.Id}' appears more than once.",
                    nameof(profile));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(video.FilePath);
            ArgumentNullException.ThrowIfNull(video.RecordingDateLabel);

            if (!Enum.IsDefined(video.Condition))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    video.Condition,
                    "The training condition is unsupported.");
            }

            if (video.SortOrder < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(profile),
                    video.SortOrder,
                    "Training video order cannot be negative.");
            }
        }
    }
}
