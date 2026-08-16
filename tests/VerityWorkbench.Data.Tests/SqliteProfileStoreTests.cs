using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Data.Profiles;
using Microsoft.Data.Sqlite;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileStoreTests
{
    [Fact]
    public async Task Existing_only_mode_does_not_create_a_missing_database()
    {
        using var database = new TestDatabase();
        var store = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.GetAllAsync());

        Assert.False(File.Exists(database.DatabasePath));
    }

    [Fact]
    public async Task AddAndLoadRoundTripsProfileAndVideos()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Ada");

        await database.Store.AddAsync(profile);

        var loaded = await database.Store.GetByIdAsync(profile.Id);

        Assert.NotNull(loaded);
        AssertProfileEqual(profile, loaded);
        Assert.True(File.Exists(database.DatabasePath));
    }

    [Fact]
    public async Task NewStoreInstanceLoadsExistingDatabaseAfterRestart()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Restart test");
        await database.Store.AddAsync(profile);

        var restartedStore = new SqliteProfileStore(database.DatabasePath);
        var loaded = await restartedStore.GetByIdAsync(profile.Id);

        Assert.NotNull(loaded);
        AssertProfileEqual(profile, loaded);
    }

    [Fact]
    public async Task CleanDatabaseIsCreatedAtCurrentSchemaVersion()
    {
        using var database = new TestDatabase();
        await database.Store.InitializeAsync();

        Assert.Equal(4L, await ReadSchemaVersionAsync(database.DatabasePath));
    }

    [Fact]
    public async Task VersionOneDatabaseMigratesToCurrentSchemaWithWorkspaceUniqueness()
    {
        using var database = new TestDatabase();
        await CreateVersionOneDatabaseAsync(database.DatabasePath);

        await database.Store.InitializeAsync();

        Assert.Equal(4L, await ReadSchemaVersionAsync(database.DatabasePath));
        Assert.True(await HasIndexAsync(
            database.DatabasePath,
            "ux_profiles_workspace_root_nocase"));
    }

    [Fact]
    public async Task UpdateReplacesEditableFieldsAndVideoCollection()
    {
        using var database = new TestDatabase();
        var original = CreateProfile("Before");
        await database.Store.AddAsync(original);

        var updatedAt = original.UpdatedAtUtc.AddHours(3);
        var replacementVideo = new StoredTrainingVideo(
            Guid.NewGuid(),
            @"D:\media\replacement.mp4",
            "session-b",
            TrainingCondition.VerifiedIntentionalDeception,
            IsArchived: false,
            SortOrder: 0);
        var updated = original with
        {
            DisplayName = "After",
            Readiness = "NeedsProcessing",
            UpdatedAtUtc = updatedAt,
            TrainingVideos = [replacementVideo],
        };

        await database.Store.UpdateAsync(updated, original.UpdatedAtUtc);

        var loaded = await database.Store.GetByIdAsync(original.Id);
        Assert.NotNull(loaded);
        AssertProfileEqual(updated, loaded);
        Assert.Equal(original.CreatedAtUtc, loaded.CreatedAtUtc);
    }

    [Fact]
    public async Task UpdateRejectsWorkspaceOrDownloadLocationChanges()
    {
        using var database = new TestDatabase();
        var original = CreateProfile("Fixed locations");
        await database.Store.AddAsync(original);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Store.UpdateAsync(original with
            {
                WorkspaceRoot = @"D:\profiles\moved",
                UpdatedAtUtc = original.UpdatedAtUtc.AddMinutes(1),
            }, original.UpdatedAtUtc));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Store.UpdateAsync(original with
            {
                DownloadStagingRoot = @"D:\profiles\different-downloads",
                UpdatedAtUtc = original.UpdatedAtUtc.AddMinutes(1),
            }, original.UpdatedAtUtc));

        var unchanged = await database.Store.GetByIdAsync(original.Id);
        Assert.NotNull(unchanged);
        AssertProfileEqual(original, unchanged);
    }

    [Fact]
    public async Task UpdateDoesNotRewriteLocationCasing()
    {
        using var database = new TestDatabase();
        var original = CreateProfile("Location casing");
        await database.Store.AddAsync(original);

        await database.Store.UpdateAsync(
            original with
            {
                DisplayName = "Renamed without moving",
                WorkspaceRoot = original.WorkspaceRoot.ToUpperInvariant(),
                DownloadStagingRoot = original.DownloadStagingRoot!.ToUpperInvariant(),
                UpdatedAtUtc = original.UpdatedAtUtc.AddMinutes(1),
            },
            original.UpdatedAtUtc);

        var loaded = await database.Store.GetByIdAsync(original.Id);
        Assert.NotNull(loaded);
        Assert.Equal(original.WorkspaceRoot, loaded.WorkspaceRoot);
        Assert.Equal(original.DownloadStagingRoot, loaded.DownloadStagingRoot);
        Assert.Equal("Renamed without moving", loaded.DisplayName);
    }

    [Fact]
    public async Task ArchivedVideosAreRetainedAndReturnedInStoredOrder()
    {
        using var database = new TestDatabase();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var profile = CreateProfile("Archive test") with
        {
            TrainingVideos =
            [
                new StoredTrainingVideo(
                    secondId,
                    @"D:\media\later.mp4",
                    "later",
                    TrainingCondition.VerifiedSincereTruth,
                    IsArchived: false,
                    SortOrder: 1),
                new StoredTrainingVideo(
                    firstId,
                    @"D:\media\earlier.mp4",
                    "earlier",
                    TrainingCondition.VerifiedIntentionalDeception,
                    IsArchived: true,
                    SortOrder: 0),
            ],
        };

        await database.Store.AddAsync(profile);

        var loaded = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(loaded);
        Assert.Collection(
            loaded.TrainingVideos,
            video =>
            {
                Assert.Equal(firstId, video.Id);
                Assert.True(video.IsArchived);
            },
            video =>
            {
                Assert.Equal(secondId, video.Id);
                Assert.False(video.IsArchived);
            });
    }

    [Fact]
    public async Task AddRejectsCaseInsensitiveDuplicateDisplayName()
    {
        using var database = new TestDatabase();
        await database.Store.AddAsync(CreateProfile("Example Profile"));

        var exception = await Assert.ThrowsAsync<ProfileNameConflictException>(
            () => database.Store.AddAsync(CreateProfile("eXaMpLe pRoFiLe") with
            {
                WorkspaceRoot = @"D:\profiles\different-workspace",
            }));

        Assert.Equal("eXaMpLe pRoFiLe", exception.DisplayName);
        Assert.Single(await database.Store.GetAllAsync());
    }

    [Fact]
    public async Task AddRejectsNonAsciiCaseVariantDisplayName()
    {
        using var database = new TestDatabase();
        await database.Store.AddAsync(CreateProfile("Åsa"));

        await Assert.ThrowsAsync<ProfileNameConflictException>(
            () => database.Store.AddAsync(CreateProfile("åSA") with
            {
                WorkspaceRoot = @"D:\profiles\different-unicode-name",
            }));
    }

    [Fact]
    public async Task AddRejectsCaseInsensitiveDuplicateWorkspaceRoot()
    {
        using var database = new TestDatabase();
        var first = CreateProfile("First workspace");
        var duplicateRoot = first.WorkspaceRoot.ToUpperInvariant();
        await database.Store.AddAsync(first);

        var exception = await Assert.ThrowsAsync<ProfileWorkspaceConflictException>(
            () => database.Store.AddAsync(CreateProfile("Second workspace") with
            {
                WorkspaceRoot = duplicateRoot,
            }));

        Assert.Equal(duplicateRoot, exception.WorkspaceRoot);
        Assert.Single(await database.Store.GetAllAsync());
    }

    [Fact]
    public async Task AddRejectsNonAsciiCaseVariantWorkspaceRoot()
    {
        using var database = new TestDatabase();
        var first = CreateProfile("Unicode workspace") with
        {
            WorkspaceRoot = @"D:\profiles\Åsa",
        };
        await database.Store.AddAsync(first);

        await Assert.ThrowsAsync<ProfileWorkspaceConflictException>(
            () => database.Store.AddAsync(CreateProfile("Other profile") with
            {
                WorkspaceRoot = @"D:\PROFILES\åSA",
            }));
    }

    [Fact]
    public async Task UpdateRejectsCaseInsensitiveDuplicateDisplayName()
    {
        using var database = new TestDatabase();
        var first = CreateProfile("First");
        var second = CreateProfile("Second");
        await database.Store.AddAsync(first);
        await database.Store.AddAsync(second);

        await Assert.ThrowsAsync<ProfileNameConflictException>(
            () => database.Store.UpdateAsync(second with
            {
                DisplayName = "FIRST",
                UpdatedAtUtc = second.UpdatedAtUtc.AddMinutes(1),
            }, second.UpdatedAtUtc));

        var unchanged = await database.Store.GetByIdAsync(second.Id);
        Assert.NotNull(unchanged);
        Assert.Equal("Second", unchanged.DisplayName);
    }

    [Fact]
    public async Task UpdateMissingProfileDoesNotInsertIt()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Missing");

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => database.Store.UpdateAsync(profile, profile.UpdatedAtUtc));

        Assert.Empty(await database.Store.GetAllAsync());
    }

    [Fact]
    public async Task UpdateRejectsStaleExpectedTimestampWithoutOverwritingNewerData()
    {
        using var database = new TestDatabase();
        var original = CreateProfile("Concurrent edit");
        await database.Store.AddAsync(original);

        var firstUpdate = original with
        {
            DisplayName = "First editor won",
            UpdatedAtUtc = original.UpdatedAtUtc.AddMinutes(1),
        };
        await database.Store.UpdateAsync(firstUpdate, original.UpdatedAtUtc);

        var staleUpdate = original with
        {
            DisplayName = "Stale editor",
            UpdatedAtUtc = original.UpdatedAtUtc.AddMinutes(2),
        };
        var exception = await Assert.ThrowsAsync<ProfileConcurrencyConflictException>(
            () => database.Store.UpdateAsync(staleUpdate, original.UpdatedAtUtc));

        Assert.Equal(original.Id, exception.ProfileId);
        Assert.Equal(original.UpdatedAtUtc, exception.ExpectedUpdatedAtUtc);
        var loaded = await database.Store.GetByIdAsync(original.Id);
        Assert.NotNull(loaded);
        Assert.Equal(firstUpdate.DisplayName, loaded.DisplayName);
        Assert.Equal(firstUpdate.UpdatedAtUtc, loaded.UpdatedAtUtc);
    }

    [Fact]
    public async Task DeletedTrainingVideoContentIsSecurelyRemovedFromDatabaseFile()
    {
        using var database = new TestDatabase();
        var marker = "SECURE_DELETE_" + new string('Q', 240);
        var profile = CreateProfile("Secure delete") with
        {
            TrainingVideos =
            [
                new StoredTrainingVideo(
                    Guid.NewGuid(),
                    @"D:\media\secure-delete.mp4",
                    marker,
                    TrainingCondition.VerifiedSincereTruth,
                    IsArchived: false,
                    SortOrder: 0),
            ],
        };
        await database.Store.AddAsync(profile);
        var markerBytes = System.Text.Encoding.UTF8.GetBytes(marker);
        Assert.True(File.ReadAllBytes(database.DatabasePath).AsSpan().IndexOf(markerBytes) >= 0);

        await database.Store.UpdateAsync(
            profile with
            {
                UpdatedAtUtc = profile.UpdatedAtUtc.AddMinutes(1),
                TrainingVideos = [],
            },
            profile.UpdatedAtUtc);

        Assert.True(File.ReadAllBytes(database.DatabasePath).AsSpan().IndexOf(markerBytes) < 0);
    }

    [Fact]
    public async Task LoadRejectsMalformedStoredTrainingVideoPath()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Corrupt path");
        await database.Store.AddAsync(profile);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE training_videos SET file_path = $path WHERE profile_id = $profileId;";
            command.Parameters.AddWithValue("$path", "relative-video.mp4");
            command.Parameters.AddWithValue("$profileId", profile.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        var reopenedStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => reopenedStore.GetByIdAsync(profile.Id));
    }

    private static StoredProfile CreateProfile(string name)
    {
        var createdAt = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        return new StoredProfile(
            Guid.NewGuid(),
            name,
            $@"D:\profiles\{name.Replace(' ', '-')}",
            @"D:\profiles\downloads",
            "Draft",
            createdAt,
            createdAt.AddMinutes(5),
            [
                new StoredTrainingVideo(
                    Guid.NewGuid(),
                    @"D:\media\truth.mp4",
                    "2026/01/01",
                    TrainingCondition.VerifiedSincereTruth,
                    IsArchived: false,
                    SortOrder: 0),
                new StoredTrainingVideo(
                    Guid.NewGuid(),
                    @"D:\media\deception.mp4",
                    "recording two",
                    TrainingCondition.VerifiedIntentionalDeception,
                    IsArchived: false,
                    SortOrder: 1),
            ]);
    }

    private static async Task CreateVersionOneDatabaseAsync(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE profiles (
                id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                workspace_root TEXT NOT NULL,
                download_staging_root TEXT NULL,
                readiness TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE training_videos (
                id TEXT NOT NULL PRIMARY KEY,
                profile_id TEXT NOT NULL,
                file_path TEXT NOT NULL,
                recording_date_label TEXT NOT NULL,
                training_condition TEXT NOT NULL,
                is_archived INTEGER NOT NULL CHECK (is_archived IN (0, 1)),
                sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_training_videos_profile_order
                ON training_videos(profile_id, sort_order, id);

            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> HasIndexAsync(string databasePath, string indexName)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_index_list('profiles') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", indexName);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private static void AssertProfileEqual(StoredProfile expected, StoredProfile actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.WorkspaceRoot, actual.WorkspaceRoot);
        Assert.Equal(expected.DownloadStagingRoot, actual.DownloadStagingRoot);
        Assert.Equal(expected.Readiness, actual.Readiness);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.UpdatedAtUtc, actual.UpdatedAtUtc);
        Assert.Equal(expected.TrainingVideos.OrderBy(video => video.SortOrder), actual.TrainingVideos);
    }
}
