using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileStoreProcessingJobWorkspaceCleanupTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 18, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VersionSevenDatabaseMigratesExistingJobWithUncleanedWorkspace()
    {
        using var database = new TestDatabase();
        var profile = await AddProfileAsync(database.Store, "Version seven cleanup migration");
        var jobId = Guid.NewGuid();
        await InsertJobAsync(
            database.DatabasePath,
            profile.Id,
            jobId,
            ProcessingJobState.Completed,
            "Processing/migrated-job",
            BaseTime.AddMinutes(2));

        await using (var connection = await OpenAsync(database.DatabasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE processing_jobs DROP COLUMN workspace_cleaned_utc;
                PRAGMA user_version = 7;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await restarted.InitializeAsync();

        Assert.Equal(8L, await ReadSchemaVersionAsync(database.DatabasePath));
        Assert.True(await TableHasColumnAsync(
            database.DatabasePath,
            "processing_jobs",
            "workspace_cleaned_utc"));
        var stored = await restarted.GetProcessingJobAsync(jobId);
        Assert.NotNull(stored);
        Assert.Null(stored.WorkspaceCleanedAtUtc);
        Assert.NotNull(await restarted.GetByIdAsync(profile.Id));
    }

    [Theory]
    [InlineData(ProcessingJobState.Completed)]
    [InlineData(ProcessingJobState.Cancelled)]
    [InlineData(ProcessingJobState.Failed)]
    [InlineData(ProcessingJobState.Interrupted)]
    public async Task TerminalJobCleanupRoundTripsWithoutChangingOutcome(
        ProcessingJobState terminalState)
    {
        using var database = new TestDatabase();
        var profile = await AddProfileAsync(database.Store, $"Cleanup {terminalState}");
        var jobId = Guid.NewGuid();
        var jobPath = $"Processing/{terminalState.ToString().ToLowerInvariant()}-job";
        var updatedAt = BaseTime.AddMinutes(2);
        var cleanedAt = BaseTime.AddMinutes(3);
        await InsertJobAsync(
            database.DatabasePath,
            profile.Id,
            jobId,
            terminalState,
            jobPath,
            updatedAt);
        var before = await database.Store.GetProcessingJobAsync(jobId);
        Assert.NotNull(before);
        var profileBefore = await database.Store.GetByIdAsync(profile.Id);

        Assert.True(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            profile.Id,
            jobId,
            terminalState,
            jobPath,
            cleanedAt));

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var stored = await restarted.GetProcessingJobAsync(jobId);
        Assert.Equal(before with { WorkspaceCleanedAtUtc = cleanedAt }, stored);
        var profileAfter = await restarted.GetByIdAsync(profile.Id);
        Assert.NotNull(profileBefore);
        Assert.NotNull(profileAfter);
        Assert.Equal(profileBefore.Id, profileAfter.Id);
        Assert.Equal(profileBefore.Readiness, profileAfter.Readiness);
        Assert.Equal(profileBefore.UpdatedAtUtc, profileAfter.UpdatedAtUtc);
        Assert.Equal(profileBefore.TrainingVideos.ToArray(), profileAfter.TrainingVideos.ToArray());
    }

    [Fact]
    public async Task CleanupRefusesActiveStaleForeignMissingAndAlreadyCleanedJobs()
    {
        using var database = new TestDatabase();
        var firstProfile = await AddProfileAsync(database.Store, "Cleanup refusal one");
        var secondProfile = await AddProfileAsync(database.Store, "Cleanup refusal two");
        var completedJobId = Guid.NewGuid();
        var queuedJobId = Guid.NewGuid();
        var runningJobId = Guid.NewGuid();
        const string completedPath = "Processing/completed-job";
        await InsertJobAsync(
            database.DatabasePath,
            firstProfile.Id,
            completedJobId,
            ProcessingJobState.Completed,
            completedPath,
            BaseTime.AddMinutes(2));
        await InsertJobAsync(
            database.DatabasePath,
            firstProfile.Id,
            queuedJobId,
            ProcessingJobState.Queued,
            "Processing/queued-job",
            BaseTime.AddMinutes(2));
        await InsertJobAsync(
            database.DatabasePath,
            secondProfile.Id,
            runningJobId,
            ProcessingJobState.Running,
            "Processing/running-job",
            BaseTime.AddMinutes(2));

        Assert.False(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            firstProfile.Id,
            queuedJobId,
            ProcessingJobState.Completed,
            "Processing/queued-job",
            BaseTime.AddMinutes(3)));
        Assert.False(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            secondProfile.Id,
            runningJobId,
            ProcessingJobState.Completed,
            "Processing/running-job",
            BaseTime.AddMinutes(3)));
        Assert.False(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            firstProfile.Id,
            completedJobId,
            ProcessingJobState.Failed,
            completedPath,
            BaseTime.AddMinutes(3)));
        Assert.False(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            firstProfile.Id,
            completedJobId,
            ProcessingJobState.Completed,
            "Processing/COMPLETED-job",
            BaseTime.AddMinutes(3)));
        Assert.False(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            firstProfile.Id,
            completedJobId,
            ProcessingJobState.Completed,
            completedPath,
            BaseTime.AddMinutes(1)));
        Assert.False(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            secondProfile.Id,
            completedJobId,
            ProcessingJobState.Completed,
            completedPath,
            BaseTime.AddMinutes(3)));
        Assert.False(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            firstProfile.Id,
            Guid.NewGuid(),
            ProcessingJobState.Completed,
            completedPath,
            BaseTime.AddMinutes(3)));
        Assert.Null((await database.Store.GetProcessingJobAsync(completedJobId))!.WorkspaceCleanedAtUtc);

        Assert.True(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            firstProfile.Id,
            completedJobId,
            ProcessingJobState.Completed,
            completedPath,
            BaseTime.AddMinutes(3)));
        Assert.False(await database.Store.MarkProcessingJobWorkspaceCleanedAsync(
            firstProfile.Id,
            completedJobId,
            ProcessingJobState.Completed,
            completedPath,
            BaseTime.AddMinutes(4)));
        Assert.Equal(
            BaseTime.AddMinutes(3),
            (await database.Store.GetProcessingJobAsync(completedJobId))!.WorkspaceCleanedAtUtc);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            database.Store.MarkProcessingJobWorkspaceCleanedAsync(
                firstProfile.Id,
                queuedJobId,
                ProcessingJobState.Queued,
                "Processing/queued-job",
                BaseTime.AddMinutes(3)));
    }

    [Fact]
    public async Task ConcurrentDuplicateCleanupMarksExactlyOnce()
    {
        using var database = new TestDatabase();
        var profile = await AddProfileAsync(database.Store, "Concurrent cleanup");
        var jobId = Guid.NewGuid();
        const string jobPath = "Processing/concurrent-job";
        await InsertJobAsync(
            database.DatabasePath,
            profile.Id,
            jobId,
            ProcessingJobState.Interrupted,
            jobPath,
            BaseTime.AddMinutes(2));
        var firstStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var secondStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var cleanedAt = BaseTime.AddMinutes(3);

        var results = await Task.WhenAll(
            firstStore.MarkProcessingJobWorkspaceCleanedAsync(
                profile.Id,
                jobId,
                ProcessingJobState.Interrupted,
                jobPath,
                cleanedAt),
            secondStore.MarkProcessingJobWorkspaceCleanedAsync(
                profile.Id,
                jobId,
                ProcessingJobState.Interrupted,
                jobPath,
                cleanedAt));

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        Assert.Equal(
            cleanedAt,
            (await database.Store.GetProcessingJobAsync(jobId))!.WorkspaceCleanedAtUtc);
    }

    [Fact]
    public async Task SchemaRejectsCleanupForActiveJobOrBeforeTerminalUpdate()
    {
        using var database = new TestDatabase();
        var profile = await AddProfileAsync(database.Store, "Cleanup schema invariants");
        var queuedJobId = Guid.NewGuid();
        var completedJobId = Guid.NewGuid();
        await InsertJobAsync(
            database.DatabasePath,
            profile.Id,
            queuedJobId,
            ProcessingJobState.Queued,
            "Processing/queued-schema-job",
            BaseTime.AddMinutes(2));
        await InsertJobAsync(
            database.DatabasePath,
            profile.Id,
            completedJobId,
            ProcessingJobState.Completed,
            "Processing/completed-schema-job",
            BaseTime.AddMinutes(2));

        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            database.DatabasePath,
            "UPDATE processing_jobs SET workspace_cleaned_utc = $cleanedAtUtc WHERE id = $id;",
            queuedJobId,
            BaseTime.AddMinutes(3)));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            database.DatabasePath,
            "UPDATE processing_jobs SET workspace_cleaned_utc = $cleanedAtUtc WHERE id = $id;",
            completedJobId,
            BaseTime.AddMinutes(1)));
    }

    [Theory]
    [InlineData(ProcessingJobState.Running, 3)]
    [InlineData(ProcessingJobState.Completed, 1)]
    public async Task ReadRefusesCorruptCleanupTimestampInvariants(
        ProcessingJobState state,
        int cleanedAtMinute)
    {
        using var database = new TestDatabase();
        var profile = await AddProfileAsync(database.Store, $"Corrupt cleanup {state}");
        var jobId = Guid.NewGuid();
        await InsertJobAsync(
            database.DatabasePath,
            profile.Id,
            jobId,
            state,
            $"Processing/corrupt-{state.ToString().ToLowerInvariant()}-job",
            BaseTime.AddMinutes(2));
        await ExecuteAsync(
            database.DatabasePath,
            "PRAGMA ignore_check_constraints = ON; " +
            "UPDATE processing_jobs SET workspace_cleaned_utc = $cleanedAtUtc WHERE id = $id;",
            jobId,
            BaseTime.AddMinutes(cleanedAtMinute));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            database.Store.GetProcessingJobAsync(jobId));
    }

    [Fact]
    public async Task ReadRefusesMalformedCleanupTimestamp()
    {
        using var database = new TestDatabase();
        var profile = await AddProfileAsync(database.Store, "Malformed cleanup timestamp");
        var jobId = Guid.NewGuid();
        await InsertJobAsync(
            database.DatabasePath,
            profile.Id,
            jobId,
            ProcessingJobState.Completed,
            "Processing/malformed-cleanup-job",
            BaseTime.AddMinutes(2));
        await using (var connection = await OpenAsync(database.DatabasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE processing_jobs SET workspace_cleaned_utc = 'not-a-timestamp' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", jobId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            database.Store.GetProcessingJobAsync(jobId));
    }

    private static async Task<StoredProfile> AddProfileAsync(
        SqliteProfileStore store,
        string displayName)
    {
        var profile = new StoredProfile(
            Guid.NewGuid(),
            displayName,
            $@"D:\profiles\{displayName.Replace(' ', '-')}",
            null,
            ProfileReadiness.Draft.ToString(),
            BaseTime,
            BaseTime,
            [
                new StoredTrainingVideo(
                    Guid.NewGuid(),
                    $@"D:\media\{displayName.Replace(' ', '-')}.mp4",
                    "recording-one",
                    TrainingCondition.VerifiedSincereTruth,
                    IsArchived: false,
                    SortOrder: 0),
            ]);
        await store.AddAsync(profile);
        return profile;
    }

    private static async Task InsertJobAsync(
        string databasePath,
        Guid profileId,
        Guid jobId,
        ProcessingJobState state,
        string workspaceRelativePath,
        DateTimeOffset updatedAtUtc)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_jobs (
                id,
                profile_id,
                kind,
                state,
                completed_item_count,
                total_item_count,
                completed_bytes,
                total_bytes,
                workspace_relative_path,
                error,
                created_utc,
                updated_utc,
                workspace_cleaned_utc)
            VALUES (
                $id,
                $profileId,
                'LocalMediaIngest',
                $state,
                $completedItemCount,
                1,
                $completedBytes,
                100,
                $workspaceRelativePath,
                $error,
                $createdUtc,
                $updatedUtc,
                NULL);
            """;
        var completed = state == ProcessingJobState.Completed;
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$completedItemCount", completed ? 1 : 0);
        command.Parameters.AddWithValue("$completedBytes", completed ? 100 : 0);
        command.Parameters.AddWithValue("$workspaceRelativePath", workspaceRelativePath);
        command.Parameters.AddWithValue(
            "$error",
            state is ProcessingJobState.Failed or ProcessingJobState.Interrupted
                ? "original terminal outcome"
                : DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", BaseTime.AddMinutes(1).ToString("O"));
        command.Parameters.AddWithValue("$updatedUtc", updatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(
        string databasePath,
        string sql,
        Guid jobId,
        DateTimeOffset cleanedAtUtc)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        command.Parameters.AddWithValue("$cleanedAtUtc", cleanedAtUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableHasColumnAsync(
        string databasePath,
        string tableName,
        string columnName)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
