using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileStoreMediaValidationTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 15, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VersionThreeDatabaseMigratesValidationSchemaWithoutLosingMedia()
    {
        using var database = new TestDatabase();
        var profileId = Guid.NewGuid();
        var mediaAssetId = Guid.NewGuid();
        await CreateVersionThreeDatabaseAsync(database.DatabasePath, profileId, mediaAssetId);

        var store = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await store.InitializeAsync();

        Assert.Equal(5L, await ReadSchemaVersionAsync(database.DatabasePath));
        Assert.True(await TableHasColumnAsync(database.DatabasePath, "media_assets", "probe_failure"));
        Assert.True(await TableExistsAsync(database.DatabasePath, "media_validation_results"));
        Assert.True(await TableExistsAsync(database.DatabasePath, "media_validation_job_assets"));
        var asset = Assert.Single(await store.GetMediaAssetsAsync(profileId));
        Assert.Equal(mediaAssetId, asset.Id);
        Assert.Equal(MediaAssetState.AwaitingProbe, asset.State);
        Assert.Null(asset.ValidationFailure);
    }

    [Fact]
    public async Task ValidationBatchPersistsImmutableResultsAndValidatedReadinessAcrossEditAndRestart()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Validated lifecycle", 2);
        var ingestedAt = profile.UpdatedAtUtc;
        var jobId = Guid.NewGuid();

        var job = await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            ingestedAt,
            jobId,
            "Processing/validation/job-one",
            BaseTime.AddMinutes(3));
        Assert.Equal(ProcessingJobKind.MediaValidation, job.Kind);
        Assert.Equal(2, job.TotalItemCount);
        Assert.Equal(ProfileReadiness.ValidatingMedia.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
        var snapshotted = await database.Store.GetMediaAssetsForValidationJobAsync(jobId);
        Assert.Equal(2, snapshotted.Count);

        var firstResult = Result(snapshotted[0].Id, 'a', BaseTime.AddMinutes(4));
        var secondResult = Result(snapshotted[1].Id, 'b', BaseTime.AddMinutes(4));
        await database.Store.CompleteMediaValidationJobAsync(
            jobId,
            [
                Success(firstResult),
                Success(secondResult),
            ],
            BaseTime.AddMinutes(5));

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var loaded = (await restarted.GetByIdAsync(profile.Id))!;
        Assert.Equal(ProfileReadiness.MediaValidated.ToString(), loaded.Readiness);
        Assert.All(await restarted.GetMediaAssetsAsync(profile.Id),
            asset => Assert.Equal(MediaAssetState.Validated, asset.State));
        Assert.Equal(firstResult, await restarted.GetMediaValidationResultAsync(firstResult.MediaAssetId));
        Assert.Equal(2, (await restarted.GetMediaValidationResultsAsync(profile.Id)).Count);
        Assert.Equal(ProcessingJobState.Completed,
            (await restarted.GetProcessingJobAsync(jobId))!.State);

        await restarted.UpdateAsync(
            loaded with
            {
                DisplayName = "Validated metadata edit",
                Readiness = ProfileReadiness.MediaIngestedAwaitingProbe.ToString(),
                UpdatedAtUtc = BaseTime.AddMinutes(6),
            },
            loaded.UpdatedAtUtc);

        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await restarted.GetByIdAsync(profile.Id))!.Readiness);
        await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                database.DatabasePath,
                "UPDATE media_validation_results SET video_codec = 'changed' WHERE media_asset_id = $id;",
                firstResult.MediaAssetId));
    }

    [Fact]
    public async Task FailedValidationStoresNoRawPathOrJsonAndArchivedFailureDoesNotBlockReadiness()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Failure archive", 2);
        var jobId = Guid.NewGuid();
        await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "Processing/validation/failure",
            BaseTime.AddMinutes(3));
        var assets = await database.Store.GetMediaAssetsForValidationJobAsync(jobId);
        var privateOutput = "D:\\private\\subject.mp4\n{\"streams\":[{\"codec\":\"bad\"}]}";

        await database.Store.CompleteMediaValidationJobAsync(
            jobId,
            [
                Success(Result(assets[0].Id, 'c', BaseTime.AddMinutes(4))),
                new MediaValidationRegistration(
                    assets[1].Id,
                    MediaAssetState.ValidationFailed,
                    null,
                    privateOutput),
            ],
            BaseTime.AddMinutes(5));

        var storedAssets = await database.Store.GetMediaAssetsAsync(profile.Id);
        var failed = Assert.Single(storedAssets, asset => asset.State == MediaAssetState.ValidationFailed);
        Assert.Equal(
            "Media validation failed; detailed tool output was not retained.",
            failed.ValidationFailure);
        Assert.DoesNotContain("private", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database.DatabasePath)));
        Assert.Null(await database.Store.GetMediaValidationResultAsync(failed.Id));
        Assert.Equal(ProfileReadiness.MediaValidationFailed.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);

        var loaded = (await database.Store.GetByIdAsync(profile.Id))!;
        await database.Store.UpdateAsync(
            loaded with
            {
                UpdatedAtUtc = BaseTime.AddMinutes(6),
                TrainingVideos = loaded.TrainingVideos
                    .Select(video => video.MediaAssetId == failed.Id
                        ? video with { IsArchived = true }
                        : video)
                    .ToArray(),
            },
            loaded.UpdatedAtUtc);

        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
    }

    [Fact]
    public async Task ValidationCompletionIsAtomicAndCancellationRestoresAwaitingReadiness()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Atomic validation", 2);
        var jobId = Guid.NewGuid();
        await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "Processing/validation/atomic",
            BaseTime.AddMinutes(3));
        var assets = await database.Store.GetMediaAssetsForValidationJobAsync(jobId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CompleteMediaValidationJobAsync(
                jobId,
                [Success(Result(assets[0].Id, 'd', BaseTime.AddMinutes(4)))],
                BaseTime.AddMinutes(5)));

        Assert.All(await database.Store.GetMediaAssetsAsync(profile.Id), asset =>
        {
            Assert.Equal(MediaAssetState.AwaitingProbe, asset.State);
            Assert.Null(asset.ValidationFailure);
        });
        Assert.Empty(await database.Store.GetMediaValidationResultsAsync(profile.Id));
        Assert.Equal(ProcessingJobState.Queued,
            (await database.Store.GetProcessingJobAsync(jobId))!.State);
        Assert.True(await database.Store.TerminateProcessingJobAsync(
            jobId,
            ProcessingJobState.Cancelled,
            error: null,
            BaseTime.AddMinutes(6)));
        Assert.Equal(ProfileReadiness.MediaIngestedAwaitingProbe.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
    }

    [Fact]
    public async Task ValidationRetryClearsFailureSecurelyAndCompletesOnlyFailedAsset()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Retry validation", 1);
        var firstJobId = Guid.NewGuid();
        await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            firstJobId,
            "Processing/validation/first",
            BaseTime.AddMinutes(3));
        var asset = Assert.Single(await database.Store.GetMediaAssetsForValidationJobAsync(firstJobId));
        var marker = "VALIDATION_FAILURE_MARKER_" + new string('Q', 180);
        await database.Store.CompleteMediaValidationJobAsync(
            firstJobId,
            [new MediaValidationRegistration(
                asset.Id,
                MediaAssetState.ValidationFailed,
                null,
                marker)],
            BaseTime.AddMinutes(4));
        Assert.Contains(marker, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database.DatabasePath)));

        var failedProfile = (await database.Store.GetByIdAsync(profile.Id))!;
        var retryJobId = Guid.NewGuid();
        var retryJob = await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            failedProfile.UpdatedAtUtc,
            retryJobId,
            "Processing/validation/retry",
            BaseTime.AddMinutes(5));
        Assert.Equal(1, retryJob.TotalItemCount);
        await database.Store.CompleteMediaValidationJobAsync(
            retryJobId,
            [Success(Result(asset.Id, 'e', BaseTime.AddMinutes(6)))],
            BaseTime.AddMinutes(7));

        var retried = Assert.Single(await database.Store.GetMediaAssetsAsync(profile.Id));
        Assert.Equal(MediaAssetState.Validated, retried.State);
        Assert.Null(retried.ValidationFailure);
        Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database.DatabasePath)));
        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
    }

    [Fact]
    public async Task RecoveryRestoresFailedReadinessForAnInterruptedRetry()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Recovery validation", 1);
        var firstJobId = Guid.NewGuid();
        await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            firstJobId,
            "Processing/validation/failed",
            BaseTime.AddMinutes(3));
        var asset = Assert.Single(await database.Store.GetMediaAssetsForValidationJobAsync(firstJobId));
        await database.Store.CompleteMediaValidationJobAsync(
            firstJobId,
            [new MediaValidationRegistration(
                asset.Id,
                MediaAssetState.ValidationFailed,
                null,
                "Unsupported media streams.")],
            BaseTime.AddMinutes(4));
        var failedProfile = (await database.Store.GetByIdAsync(profile.Id))!;
        var retryJobId = Guid.NewGuid();
        await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            failedProfile.UpdatedAtUtc,
            retryJobId,
            "Processing/validation/interrupted",
            BaseTime.AddMinutes(5));

        var recovered = await database.Store.RecoverInterruptedJobsAsync(
            BaseTime.AddMinutes(6),
            BaseTime.AddMinutes(7));

        Assert.Equal(1, recovered);
        Assert.Equal(ProcessingJobState.Interrupted,
            (await database.Store.GetProcessingJobAsync(retryJobId))!.State);
        Assert.Equal(ProfileReadiness.MediaValidationFailed.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
    }

    [Fact]
    public async Task ValidationStartUsesProfileAndActiveJobConcurrency()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Validation concurrency", 1);
        var staleJobId = Guid.NewGuid();
        await Assert.ThrowsAsync<ProfileConcurrencyConflictException>(() =>
            database.Store.StartMediaValidationJobAsync(
                profile.Id,
                profile.UpdatedAtUtc.AddMinutes(-1),
                staleJobId,
                "Processing/validation/stale",
                BaseTime.AddMinutes(3)));
        Assert.Null(await database.Store.GetProcessingJobAsync(staleJobId));
        Assert.Equal(ProfileReadiness.MediaIngestedAwaitingProbe.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);

        var activeJobId = Guid.NewGuid();
        await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            activeJobId,
            "Processing/validation/active",
            BaseTime.AddMinutes(3));
        var secondStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await Assert.ThrowsAsync<ProfileProcessingActiveException>(() =>
            secondStore.StartMediaValidationJobAsync(
                profile.Id,
                BaseTime.AddMinutes(3),
                Guid.NewGuid(),
                "Processing/validation/second",
                BaseTime.AddMinutes(4)));
    }

    [Fact]
    public async Task ArchivingNewAwaitingAssetRestoresValidatedReadiness()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Awaiting archive", 1);
        var firstJobId = Guid.NewGuid();
        await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            firstJobId,
            "Processing/validation/original",
            BaseTime.AddMinutes(3));
        var originalAsset = Assert.Single(
            await database.Store.GetMediaAssetsForValidationJobAsync(firstJobId));
        await database.Store.CompleteMediaValidationJobAsync(
            firstJobId,
            [Success(Result(originalAsset.Id, '8', BaseTime.AddMinutes(4)))],
            BaseTime.AddMinutes(5));

        var validated = (await database.Store.GetByIdAsync(profile.Id))!;
        var addedVideo = new StoredTrainingVideo(
            Guid.NewGuid(),
            @"D:\media\new-awaiting.mp4",
            "new",
            TrainingCondition.VerifiedIntentionalDeception,
            IsArchived: false,
            SortOrder: 1);
        await database.Store.UpdateAsync(
            validated with
            {
                UpdatedAtUtc = BaseTime.AddMinutes(6),
                TrainingVideos = [.. validated.TrainingVideos, addedVideo],
            },
            validated.UpdatedAtUtc);
        var edited = (await database.Store.GetByIdAsync(profile.Id))!;
        Assert.Equal(ProfileReadiness.Draft.ToString(), edited.Readiness);

        var ingestJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            edited.UpdatedAtUtc,
            ingestJobId,
            "Processing/ingest/new",
            1,
            10,
            BaseTime.AddMinutes(7));
        await database.Store.CompleteLocalMediaIngestJobAsync(
            ingestJobId,
            [new MediaAssetRegistration(
                addedVideo.Id,
                Guid.NewGuid(),
                new string('9', 64),
                "Media/new-awaiting.mp4",
                10)],
            BaseTime.AddMinutes(8));
        var awaiting = (await database.Store.GetByIdAsync(profile.Id))!;
        Assert.Equal(ProfileReadiness.MediaIngestedAwaitingProbe.ToString(), awaiting.Readiness);

        await database.Store.UpdateAsync(
            awaiting with
            {
                UpdatedAtUtc = BaseTime.AddMinutes(9),
                TrainingVideos = awaiting.TrainingVideos
                    .Select(video => video.Id == addedVideo.Id
                        ? video with { IsArchived = true }
                        : video)
                    .ToArray(),
            },
            awaiting.UpdatedAtUtc);
        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
    }

    [Fact]
    public async Task SharedActiveMediaAssetIsValidatedOnlyOnce()
    {
        using var database = new TestDatabase();
        var videos = new[]
        {
            new StoredTrainingVideo(
                Guid.NewGuid(),
                @"D:\media\shared-one.mp4",
                "one",
                TrainingCondition.VerifiedSincereTruth,
                IsArchived: false,
                SortOrder: 0),
            new StoredTrainingVideo(
                Guid.NewGuid(),
                @"D:\media\shared-two.mp4",
                "two",
                TrainingCondition.VerifiedSincereTruth,
                IsArchived: false,
                SortOrder: 1),
        };
        var profile = new StoredProfile(
            Guid.NewGuid(),
            "Shared validation asset",
            @"D:\profiles\shared-validation-asset",
            null,
            ProfileReadiness.Draft.ToString(),
            BaseTime,
            BaseTime,
            videos);
        await database.Store.AddAsync(profile);

        var assetId = Guid.NewGuid();
        var ingestJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            ingestJobId,
            "Processing/ingest/shared",
            2,
            20,
            BaseTime.AddMinutes(1));
        var sharedHash = new string('7', 64);
        await database.Store.CompleteLocalMediaIngestJobAsync(
            ingestJobId,
            videos.Select(video => new MediaAssetRegistration(
                video.Id,
                assetId,
                sharedHash,
                "Media/shared/original.mp4",
                10)).ToArray(),
            BaseTime.AddMinutes(2));

        var ingested = (await database.Store.GetByIdAsync(profile.Id))!;
        var validationJobId = Guid.NewGuid();
        var validationJob = await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            ingested.UpdatedAtUtc,
            validationJobId,
            "Processing/validation/shared",
            BaseTime.AddMinutes(3));

        Assert.Equal(1, validationJob.TotalItemCount);
        Assert.Equal(10, validationJob.TotalBytes);
        var asset = Assert.Single(
            await database.Store.GetMediaAssetsForValidationJobAsync(validationJobId));
        Assert.Equal(assetId, asset.Id);

        await database.Store.CompleteMediaValidationJobAsync(
            validationJobId,
            [Success(Result(assetId, '7', BaseTime.AddMinutes(4)))],
            BaseTime.AddMinutes(5));

        var validated = (await database.Store.GetByIdAsync(profile.Id))!;
        Assert.Equal(ProfileReadiness.MediaValidated.ToString(), validated.Readiness);
        Assert.All(validated.TrainingVideos,
            video => Assert.Equal(assetId, video.MediaAssetId));
    }

    private static MediaValidationRegistration Success(StoredMediaValidationResult result) =>
        new(result.MediaAssetId, MediaAssetState.Validated, result, null);

    private static StoredMediaValidationResult Result(
        Guid mediaAssetId,
        char hashCharacter,
        DateTimeOffset validatedAtUtc)
    {
        var hash = new string(hashCharacter, 64);
        return new StoredMediaValidationResult(
            mediaAssetId,
            "mov,mp4,m4a,3gp,3g2,mj2",
            "isom",
            0,
            "h264",
            1920,
            1080,
            1,
            "aac",
            48_000,
            2,
            30_000_000,
            30_000,
            1_001,
            "8.1.2",
            "gcc 15.2.0",
            "--enable-version3 --enable-pthreads",
            hash,
            hash,
            "8.1.2",
            "gcc 15.2.0",
            "--enable-version3 --enable-pthreads",
            hash,
            hash,
            hash,
            DecodeCompleted: true,
            DecodedDurationMicroseconds: 30_000_000,
            validatedAtUtc);
    }

    private static async Task<StoredProfile> AddIngestedProfileAsync(
        SqliteProfileStore store,
        string name,
        int videoCount)
    {
        var videos = Enumerable.Range(0, videoCount)
            .Select(index => new StoredTrainingVideo(
                Guid.NewGuid(),
                $@"D:\media\{name}-{index}.mp4",
                $"recording-{index}",
                index % 2 == 0
                    ? TrainingCondition.VerifiedSincereTruth
                    : TrainingCondition.VerifiedIntentionalDeception,
                IsArchived: false,
                SortOrder: index))
            .ToArray();
        var profile = new StoredProfile(
            Guid.NewGuid(),
            name,
            $@"D:\profiles\{name.Replace(' ', '-')}",
            null,
            ProfileReadiness.Draft.ToString(),
            BaseTime,
            BaseTime,
            videos);
        await store.AddAsync(profile);
        var ingestJobId = Guid.NewGuid();
        await store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            ingestJobId,
            "Processing/ingest/job",
            videoCount,
            videoCount * 10L,
            BaseTime.AddMinutes(1));
        var registrations = videos
            .Select((video, index) => new MediaAssetRegistration(
                video.Id,
                Guid.NewGuid(),
                new string((char)('1' + index), 64),
                $"Media/{index}.mp4",
                10))
            .ToArray();
        await store.CompleteLocalMediaIngestJobAsync(
            ingestJobId,
            registrations,
            BaseTime.AddMinutes(2));
        return (await store.GetByIdAsync(profile.Id))!;
    }

    private static async Task CreateVersionThreeDatabaseAsync(
        string databasePath,
        Guid profileId,
        Guid mediaAssetId)
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
            PRAGMA foreign_keys = ON;
            CREATE TABLE profiles (
                id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                workspace_root TEXT NOT NULL,
                download_staging_root TEXT NULL,
                readiness TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL);
            CREATE TABLE media_assets (
                id TEXT NOT NULL PRIMARY KEY,
                profile_id TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                workspace_relative_path TEXT NOT NULL,
                byte_length INTEGER NOT NULL,
                state TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE,
                UNIQUE (profile_id, sha256));
            CREATE TABLE training_videos (
                id TEXT NOT NULL PRIMARY KEY,
                profile_id TEXT NOT NULL,
                file_path TEXT NOT NULL,
                recording_date_label TEXT NOT NULL,
                training_condition TEXT NOT NULL,
                is_archived INTEGER NOT NULL,
                sort_order INTEGER NOT NULL,
                media_asset_id TEXT NULL REFERENCES media_assets(id) ON DELETE SET NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE);
            CREATE TABLE processing_jobs (
                id TEXT NOT NULL PRIMARY KEY,
                profile_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                state TEXT NOT NULL,
                completed_item_count INTEGER NOT NULL,
                total_item_count INTEGER NOT NULL,
                completed_bytes INTEGER NOT NULL,
                total_bytes INTEGER NOT NULL,
                workspace_relative_path TEXT NOT NULL,
                error TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(id) ON DELETE CASCADE);
            PRAGMA user_version = 3;
            """;
        await command.ExecuteNonQueryAsync();

        var timestamp = BaseTime.ToString("O");
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO profiles
                (id, display_name, workspace_root, readiness, created_utc, updated_utc)
            VALUES ($profileId, 'Migrated', 'D:\profiles\migrated', 'MediaIngestedAwaitingProbe',
                    $timestamp, $timestamp);
            INSERT INTO media_assets
                (id, profile_id, sha256, workspace_relative_path, byte_length, state, created_utc, updated_utc)
            VALUES ($assetId, $profileId, $sha256, 'Media/migrated.mp4', 10, 'AwaitingProbe',
                    $timestamp, $timestamp);
            """;
        insert.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        insert.Parameters.AddWithValue("$assetId", mediaAssetId.ToString("D"));
        insert.Parameters.AddWithValue("$sha256", new string('f', 64));
        insert.Parameters.AddWithValue("$timestamp", timestamp);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(string databasePath, string tableName)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task<bool> TableHasColumnAsync(
        string databasePath,
        string tableName,
        string columnName)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", columnName);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(string databasePath, string sql, Guid id)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }
}
