using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileStoreRecordingDependencyGroupTests
{
    [Fact]
    public async Task Groups_and_assignments_round_trip_across_restart()
    {
        using var database = new TestDatabase();
        var firstGroup = new StoredRecordingDependencyGroup(Guid.NewGuid(), "Interview one");
        var secondGroup = new StoredRecordingDependencyGroup(Guid.NewGuid(), "Interview two");
        var profile = CreateProfile(
            [firstGroup, secondGroup],
            firstGroup.Id,
            secondGroup.Id) with
        {
            TrainingVideos =
            [
                CreateVideo(0, firstGroup.Id, null),
                CreateVideo(1, secondGroup.Id, null) with
                {
                    Condition = TrainingCondition.VerifiedIntentionalDeception,
                },
            ],
        };

        await database.Store.AddAsync(profile);

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var loaded = await restarted.GetByIdAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal([firstGroup, secondGroup], loaded.RecordingDependencyGroups);
        Assert.Equal(firstGroup.Id, loaded.TrainingVideos[0].RecordingDependencyGroupId);
        Assert.Equal(secondGroup.Id, loaded.TrainingVideos[1].RecordingDependencyGroupId);
        Assert.Equal(
            TrainingCondition.VerifiedIntentionalDeception,
            loaded.TrainingVideos[1].Condition);
    }

    [Fact]
    public async Task Update_preserves_group_ids_while_renaming_and_reassigning_archived_video()
    {
        using var database = new TestDatabase();
        var firstGroup = new StoredRecordingDependencyGroup(Guid.NewGuid(), "Before rename");
        var secondGroup = new StoredRecordingDependencyGroup(Guid.NewGuid(), "Second group");
        var profile = CreateProfile([firstGroup, secondGroup], firstGroup.Id, null);
        await database.Store.AddAsync(profile);

        var updated = profile with
        {
            UpdatedAtUtc = profile.UpdatedAtUtc.AddMinutes(1),
            RecordingDependencyGroups =
            [
                firstGroup with { DisplayName = "After rename" },
                secondGroup,
            ],
            TrainingVideos =
            [
                profile.TrainingVideos[0] with
                {
                    IsArchived = true,
                    RecordingDependencyGroupId = secondGroup.Id,
                },
                profile.TrainingVideos[1],
            ],
        };

        await database.Store.UpdateAsync(updated, profile.UpdatedAtUtc);

        var loaded = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(loaded);
        Assert.Contains(
            loaded.RecordingDependencyGroups,
            group => group.Id == firstGroup.Id && group.DisplayName == "After rename");
        Assert.True(loaded.TrainingVideos[0].IsArchived);
        Assert.Equal(secondGroup.Id, loaded.TrainingVideos[0].RecordingDependencyGroupId);
    }

    [Fact]
    public async Task Existing_database_rows_migrate_as_unassigned()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile([], null, null);
        await database.Store.AddAsync(profile);

        await using (var connection = await OpenAsync(database.DatabasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER audio_observation_results_immutable_update;
                DROP TABLE audio_observation_job_assets;
                DROP TABLE audio_observation_results;
                ALTER TABLE media_assets DROP COLUMN audio_observation_failure;
                ALTER TABLE processing_jobs DROP COLUMN workspace_cleaned_utc;
                DROP TRIGGER training_videos_dependency_group_profile_insert;
                DROP TRIGGER training_videos_dependency_group_profile_update;
                DROP INDEX ix_training_videos_recording_dependency_group;
                ALTER TABLE training_videos DROP COLUMN recording_dependency_group_id;
                DROP TABLE recording_dependency_groups;
                PRAGMA user_version = 5;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var migrated = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var loaded = await migrated.GetByIdAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.RecordingDependencyGroups);
        Assert.All(loaded.TrainingVideos, video => Assert.Null(video.RecordingDependencyGroupId));
    }

    [Fact]
    public async Task Duplicate_group_names_and_unknown_assignments_are_rejected()
    {
        using var database = new TestDatabase();
        var group = new StoredRecordingDependencyGroup(Guid.NewGuid(), "Session Å");
        var duplicateName = new StoredRecordingDependencyGroup(Guid.NewGuid(), "session å");
        var duplicateProfile = CreateProfile([group, duplicateName], group.Id, null);

        await Assert.ThrowsAsync<ArgumentException>(() => database.Store.AddAsync(duplicateProfile));

        var unknownProfile = CreateProfile([group], Guid.NewGuid(), null);
        await Assert.ThrowsAsync<ArgumentException>(() => database.Store.AddAsync(unknownProfile));
    }

    [Fact]
    public async Task Unassigned_group_name_is_rejected_ignoring_case()
    {
        using var database = new TestDatabase();
        var reservedGroup = new StoredRecordingDependencyGroup(Guid.NewGuid(), "UNASSIGNED");
        var profile = CreateProfile([reservedGroup], reservedGroup.Id, null);

        await Assert.ThrowsAsync<ArgumentException>(() => database.Store.AddAsync(profile));
    }

    [Fact]
    public void Summary_counts_active_assignments_and_flags_cross_group_asset_reuse()
    {
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        var sharedAssetId = Guid.NewGuid();
        var profile = CreateProfile(
            [
                new(firstGroupId, "First"),
                new(secondGroupId, "Second"),
            ],
            firstGroupId,
            secondGroupId) with
        {
            TrainingVideos =
            [
                CreateVideo(0, firstGroupId, sharedAssetId),
                CreateVideo(1, secondGroupId, sharedAssetId),
                CreateVideo(2, null, null),
                CreateVideo(3, null, null) with { IsArchived = true },
            ],
        };

        var summary = RecordingDependencyGroupSummaryBuilder.Create(profile);

        Assert.Equal(2, summary.ActiveAssignedGroupCount);
        Assert.Equal(1, summary.ActiveUnassignedVideoCount);
        var conflict = Assert.Single(summary.Conflicts);
        Assert.Equal(sharedAssetId, conflict.MediaAssetId);
        Assert.Equal(
            new[] { firstGroupId, secondGroupId }.OrderBy(id => id),
            conflict.RecordingDependencyGroupIds);
    }

    private static StoredProfile CreateProfile(
        IReadOnlyList<StoredRecordingDependencyGroup> groups,
        Guid? firstGroupId,
        Guid? secondGroupId)
    {
        var now = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        return new(
            Guid.NewGuid(),
            "Dependency groups",
            @"D:\profiles\dependency-groups",
            null,
            ProfileReadiness.Draft.ToString(),
            now,
            now,
            [
                CreateVideo(0, firstGroupId, null),
                CreateVideo(1, secondGroupId, null),
            ],
            groups);
    }

    private static StoredTrainingVideo CreateVideo(
        int sortOrder,
        Guid? groupId,
        Guid? mediaAssetId) =>
        new(
            Guid.NewGuid(),
            $@"D:\media\video-{sortOrder}.mp4",
            $"recording-{sortOrder}",
            TrainingCondition.VerifiedSincereTruth,
            IsArchived: false,
            sortOrder,
            mediaAssetId,
            groupId);

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
