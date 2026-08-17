using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileStoreMediaPreprocessingTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VersionFourDatabaseMigratesWithoutChangingValidatedMedia()
    {
        using var database = new TestDatabase();
        var setup = await AddValidatedProfileAsync(database.Store, "Version four migration", 1);
        var validation = await database.Store.GetMediaValidationResultAsync(setup.Assets[0].Id);
        await DowngradeToVersionFourAsync(database.DatabasePath);

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await restarted.InitializeAsync();

        Assert.Equal(7L, await ReadSchemaVersionAsync(database.DatabasePath));
        Assert.True(await TableHasColumnAsync(
            database.DatabasePath,
            "media_assets",
            "preprocessing_failure"));
        Assert.True(await TableExistsAsync(database.DatabasePath, "media_preprocessing_results"));
        Assert.True(await TableExistsAsync(database.DatabasePath, "media_preprocessing_job_assets"));
        var asset = Assert.Single(await restarted.GetMediaAssetsAsync(setup.Profile.Id));
        Assert.Equal(MediaAssetState.Validated, asset.State);
        Assert.Null(asset.PreprocessingFailure);
        Assert.Equal(validation, await restarted.GetMediaValidationResultAsync(asset.Id));
        Assert.Null(await restarted.GetMediaPreprocessingResultAsync(asset.Id));
    }

    [Fact]
    public async Task SuccessfulLifecyclePersistsImmutableResultAndIntegrityFailurePreservesIt()
    {
        using var database = new TestDatabase();
        var setup = await AddValidatedProfileAsync(database.Store, "Prepared lifecycle", 1);
        var jobId = Guid.NewGuid();
        var job = await database.Store.StartMediaPreprocessingJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            jobId,
            "Processing/preprocessing/job-one",
            BaseTime.AddMinutes(6));

        Assert.Equal(ProcessingJobKind.MediaPreprocessing, job.Kind);
        Assert.Equal(1, job.TotalItemCount);
        Assert.Equal(100, job.TotalBytes);
        Assert.Equal(ProfileReadiness.PreprocessingMedia.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
        var asset = Assert.Single(await database.Store.GetMediaAssetsForPreprocessingJobAsync(jobId));
        var result = PreprocessingResult(asset, BaseTime.AddMinutes(7));

        await database.Store.CompleteMediaPreprocessingJobAsync(
            jobId,
            [Success(result)],
            BaseTime.AddMinutes(8));

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var prepared = Assert.Single(await restarted.GetMediaAssetsAsync(setup.Profile.Id));
        Assert.Equal(MediaAssetState.Prepared, prepared.State);
        Assert.Null(prepared.ValidationFailure);
        Assert.Null(prepared.PreprocessingFailure);
        Assert.Equal(ProfileReadiness.MediaPrepared.ToString(),
            (await restarted.GetByIdAsync(setup.Profile.Id))!.Readiness);
        Assert.Equal(result, await restarted.GetMediaPreprocessingResultAsync(asset.Id));
        Assert.Equal(result, Assert.Single(
            await restarted.GetMediaPreprocessingResultsAsync(setup.Profile.Id)));
        Assert.Equal(ProcessingJobState.Completed,
            (await restarted.GetProcessingJobAsync(jobId))!.State);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            database.DatabasePath,
            "UPDATE media_preprocessing_results SET proxy_video_codec = 'changed' " +
            "WHERE media_asset_id = $id;",
            asset.Id));

        var profile = (await restarted.GetByIdAsync(setup.Profile.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.StartMediaValidationJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                Guid.NewGuid(),
                "Processing/validation/prepared-is-not-pending",
                BaseTime.AddMinutes(9)));
        await restarted.MarkMediaAssetsIntegrityFailedAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            [asset.Id],
            BaseTime.AddMinutes(9));

        var integrityFailed = Assert.Single(await restarted.GetMediaAssetsAsync(profile.Id));
        Assert.Equal(MediaAssetState.IntegrityFailed, integrityFailed.State);
        Assert.Null(integrityFailed.PreprocessingFailure);
        Assert.Equal(ProfileReadiness.MediaIntegrityFailed.ToString(),
            (await restarted.GetByIdAsync(profile.Id))!.Readiness);
        Assert.Equal(result, await restarted.GetMediaPreprocessingResultAsync(asset.Id));
        Assert.NotNull(await restarted.GetMediaValidationResultAsync(asset.Id));
    }

    [Fact]
    public async Task FailedItemIsSanitizedAndRetryProcessesOnlyTheFailure()
    {
        using var database = new TestDatabase();
        var setup = await AddValidatedProfileAsync(database.Store, "Preprocessing retry", 1);
        var firstJobId = Guid.NewGuid();
        await database.Store.StartMediaPreprocessingJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            firstJobId,
            "Processing/preprocessing/first",
            BaseTime.AddMinutes(6));
        var asset = Assert.Single(
            await database.Store.GetMediaAssetsForPreprocessingJobAsync(firstJobId));
        var privateOutput = "D:\\private\\subject.mp4\n{\"tool\":\"raw output\"}";

        await database.Store.CompleteMediaPreprocessingJobAsync(
            firstJobId,
            [new MediaPreprocessingRegistration(
                asset.Id,
                MediaAssetState.PreprocessingFailed,
                null,
                privateOutput)],
            BaseTime.AddMinutes(7));

        var failed = Assert.Single(await database.Store.GetMediaAssetsAsync(setup.Profile.Id));
        Assert.Equal(MediaAssetState.PreprocessingFailed, failed.State);
        Assert.Equal(
            "Media preprocessing failed; detailed tool output was not retained.",
            failed.PreprocessingFailure);
        Assert.Equal(ProfileReadiness.MediaPreprocessingFailed.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
        Assert.Null(await database.Store.GetMediaPreprocessingResultAsync(asset.Id));
        Assert.DoesNotContain(
            "private",
            System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database.DatabasePath)));

        var failedProfile = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Store.StartMediaValidationJobAsync(
                failedProfile.Id,
                failedProfile.UpdatedAtUtc,
                Guid.NewGuid(),
                "Processing/validation/not-a-retry",
                BaseTime.AddMinutes(8)));

        var retryJobId = Guid.NewGuid();
        var retry = await database.Store.StartMediaPreprocessingJobAsync(
            failedProfile.Id,
            failedProfile.UpdatedAtUtc,
            retryJobId,
            "Processing/preprocessing/retry",
            BaseTime.AddMinutes(8));
        Assert.Equal(1, retry.TotalItemCount);
        Assert.Equal(asset.Id,
            Assert.Single(await database.Store.GetMediaAssetsForPreprocessingJobAsync(retryJobId)).Id);
        await database.Store.CompleteMediaPreprocessingJobAsync(
            retryJobId,
            [Success(PreprocessingResult(asset, BaseTime.AddMinutes(9)))],
            BaseTime.AddMinutes(10));

        var prepared = Assert.Single(await database.Store.GetMediaAssetsAsync(setup.Profile.Id));
        Assert.Equal(MediaAssetState.Prepared, prepared.State);
        Assert.Null(prepared.PreprocessingFailure);
        Assert.Equal(ProfileReadiness.MediaPrepared.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
    }

    [Fact]
    public async Task StartAndCompletionAreOptimisticallyConcurrentAndAtomic()
    {
        using var database = new TestDatabase();
        var setup = await AddValidatedProfileAsync(database.Store, "Preprocessing concurrency", 1);
        var staleJobId = Guid.NewGuid();
        await Assert.ThrowsAsync<ProfileConcurrencyConflictException>(() =>
            database.Store.StartMediaPreprocessingJobAsync(
                setup.Profile.Id,
                setup.Profile.UpdatedAtUtc.AddMinutes(-1),
                staleJobId,
                "Processing/preprocessing/stale",
                BaseTime.AddMinutes(6)));
        Assert.Null(await database.Store.GetProcessingJobAsync(staleJobId));
        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);

        var jobId = Guid.NewGuid();
        await database.Store.StartMediaPreprocessingJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            jobId,
            "Processing/preprocessing/active",
            BaseTime.AddMinutes(6));
        var activeProfile = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        await Assert.ThrowsAsync<ProfileProcessingActiveException>(() =>
            database.Store.UpdateAsync(
                activeProfile with
                {
                    DisplayName = "Should not change",
                    UpdatedAtUtc = BaseTime.AddMinutes(7),
                },
                activeProfile.UpdatedAtUtc));

        var asset = Assert.Single(await database.Store.GetMediaAssetsForPreprocessingJobAsync(jobId));
        var invalid = PreprocessingResult(asset, BaseTime.AddMinutes(7)) with
        {
            ProxyWorkspaceRelativePath = "Media/other/Prepared/v1_ffffffffffff/proxy.mp4",
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CompleteMediaPreprocessingJobAsync(
                jobId,
                [Success(invalid)],
                BaseTime.AddMinutes(8)));

        Assert.Equal(MediaAssetState.Validated,
            Assert.Single(await database.Store.GetMediaAssetsAsync(setup.Profile.Id)).State);
        Assert.Null(await database.Store.GetMediaPreprocessingResultAsync(asset.Id));
        Assert.Equal(ProcessingJobState.Queued,
            (await database.Store.GetProcessingJobAsync(jobId))!.State);
        Assert.True(await database.Store.TerminateProcessingJobAsync(
            jobId,
            ProcessingJobState.Cancelled,
            null,
            BaseTime.AddMinutes(9)));
        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
    }

    [Fact]
    public async Task CancellationAndStaleRecoveryWriteNoSuccessfulResult()
    {
        using var database = new TestDatabase();
        var setup = await AddValidatedProfileAsync(database.Store, "Preprocessing recovery", 1);
        var cancelledJobId = Guid.NewGuid();
        await database.Store.StartMediaPreprocessingJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            cancelledJobId,
            "Processing/preprocessing/cancelled",
            BaseTime.AddMinutes(6));
        Assert.True(await database.Store.TerminateProcessingJobAsync(
            cancelledJobId,
            ProcessingJobState.Cancelled,
            null,
            BaseTime.AddMinutes(7)));
        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);

        var recoveredProfile = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        var interruptedJobId = Guid.NewGuid();
        await database.Store.StartMediaPreprocessingJobAsync(
            recoveredProfile.Id,
            recoveredProfile.UpdatedAtUtc,
            interruptedJobId,
            "Processing/preprocessing/interrupted",
            BaseTime.AddMinutes(8));
        Assert.Equal(1, await database.Store.RecoverInterruptedJobsAsync(
            BaseTime.AddMinutes(9),
            BaseTime.AddMinutes(10)));

        Assert.Equal(ProcessingJobState.Interrupted,
            (await database.Store.GetProcessingJobAsync(interruptedJobId))!.State);
        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
        Assert.Equal(MediaAssetState.Validated,
            Assert.Single(await database.Store.GetMediaAssetsAsync(setup.Profile.Id)).State);
        Assert.Empty(await database.Store.GetMediaPreprocessingResultsAsync(setup.Profile.Id));
    }

    [Fact]
    public async Task ArchivedFailureDoesNotBlockPreparedActiveMediaAndUnarchiveRestoresFailure()
    {
        using var database = new TestDatabase();
        var setup = await AddValidatedProfileAsync(database.Store, "Archive preprocessing failure", 2);
        var jobId = Guid.NewGuid();
        await database.Store.StartMediaPreprocessingJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            jobId,
            "Processing/preprocessing/mixed",
            BaseTime.AddMinutes(6));
        var assets = await database.Store.GetMediaAssetsForPreprocessingJobAsync(jobId);
        await database.Store.CompleteMediaPreprocessingJobAsync(
            jobId,
            [
                Success(PreprocessingResult(assets[0], BaseTime.AddMinutes(7))),
                new MediaPreprocessingRegistration(
                    assets[1].Id,
                    MediaAssetState.PreprocessingFailed,
                    null,
                    "Canonical derivative generation failed."),
            ],
            BaseTime.AddMinutes(8));
        Assert.Equal(ProfileReadiness.MediaPreprocessingFailed.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);

        var failedAssetId = assets[1].Id;
        var mixed = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        await database.Store.UpdateAsync(
            mixed with
            {
                UpdatedAtUtc = BaseTime.AddMinutes(9),
                TrainingVideos = mixed.TrainingVideos
                    .Select(video => video.MediaAssetId == failedAssetId
                        ? video with { IsArchived = true }
                        : video)
                    .ToArray(),
            },
            mixed.UpdatedAtUtc);
        Assert.Equal(ProfileReadiness.MediaPrepared.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);

        var archived = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        await database.Store.UpdateAsync(
            archived with
            {
                UpdatedAtUtc = BaseTime.AddMinutes(10),
                TrainingVideos = archived.TrainingVideos
                    .Select(video => video.MediaAssetId == failedAssetId
                        ? video with { IsArchived = false }
                        : video)
                    .ToArray(),
            },
            archived.UpdatedAtUtc);
        var unarchived = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        Assert.Equal(ProfileReadiness.MediaPreprocessingFailed.ToString(), unarchived.Readiness);

        var retryJobId = Guid.NewGuid();
        var retry = await database.Store.StartMediaPreprocessingJobAsync(
            unarchived.Id,
            unarchived.UpdatedAtUtc,
            retryJobId,
            "Processing/preprocessing/failed-only",
            BaseTime.AddMinutes(11));
        Assert.Equal(1, retry.TotalItemCount);
        Assert.Equal(failedAssetId,
            Assert.Single(await database.Store.GetMediaAssetsForPreprocessingJobAsync(retryJobId)).Id);
    }

    [Fact]
    public async Task SharedActiveAssetIsPreprocessedOnlyOnce()
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
            "Shared preprocessing asset",
            @"D:\profiles\shared-preprocessing-asset",
            null,
            ProfileReadiness.Draft.ToString(),
            BaseTime,
            BaseTime,
            videos);
        await database.Store.AddAsync(profile);
        var assetId = Guid.NewGuid();
        var assetPath = $"Media/shared_{assetId:N}/original.mp4";
        var ingestJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            ingestJobId,
            "Processing/ingest/shared",
            2,
            200,
            BaseTime.AddMinutes(1));
        await database.Store.CompleteLocalMediaIngestJobAsync(
            ingestJobId,
            videos.Select(video => new MediaAssetRegistration(
                video.Id,
                assetId,
                new string('7', 64),
                assetPath,
                100)).ToArray(),
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
        var asset = Assert.Single(await database.Store.GetMediaAssetsForValidationJobAsync(validationJobId));
        await database.Store.CompleteMediaValidationJobAsync(
            validationJobId,
            [ValidationSuccess(ValidationResult(asset.Id, BaseTime.AddMinutes(4)))],
            BaseTime.AddMinutes(5));

        var validated = (await database.Store.GetByIdAsync(profile.Id))!;
        var preprocessingJobId = Guid.NewGuid();
        var preprocessingJob = await database.Store.StartMediaPreprocessingJobAsync(
            profile.Id,
            validated.UpdatedAtUtc,
            preprocessingJobId,
            "Processing/preprocessing/shared",
            BaseTime.AddMinutes(6));
        Assert.Equal(1, preprocessingJob.TotalItemCount);
        Assert.Equal(100, preprocessingJob.TotalBytes);
        Assert.Equal(assetId,
            Assert.Single(await database.Store.GetMediaAssetsForPreprocessingJobAsync(
                preprocessingJobId)).Id);
        await database.Store.CompleteMediaPreprocessingJobAsync(
            preprocessingJobId,
            [Success(PreprocessingResult(asset, BaseTime.AddMinutes(7)))],
            BaseTime.AddMinutes(8));

        Assert.Equal(ProfileReadiness.MediaPrepared.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
        Assert.Single(await database.Store.GetMediaPreprocessingResultsAsync(profile.Id));
    }

    private static MediaPreprocessingRegistration Success(StoredMediaPreprocessingResult result) =>
        new(result.MediaAssetId, MediaAssetState.Prepared, result, null);

    private static MediaValidationRegistration ValidationSuccess(StoredMediaValidationResult result) =>
        new(result.MediaAssetId, MediaAssetState.Validated, result, null);

    private static StoredMediaPreprocessingResult PreprocessingResult(
        StoredMediaAsset asset,
        DateTimeOffset preprocessedAtUtc)
    {
        var contractHash = new string('f', 64);
        var sourceDirectory = asset.WorkspaceRelativePath[..asset.WorkspaceRelativePath.LastIndexOf('/')];
        var preparedDirectory = $"{sourceDirectory}/Prepared/v1_{contractHash[..12]}";
        return new StoredMediaPreprocessingResult(
            asset.Id,
            asset.Sha256,
            asset.ByteLength,
            "verityworkbench.media-preprocessing.v1",
            contractHash,
            preparedDirectory + "/proxy.mp4",
            new string('a', 64),
            1_000,
            "mp4",
            "mpeg4",
            "yuv420p",
            1280,
            720,
            30,
            1,
            "aac",
            48_000,
            2,
            30_000_000,
            preparedDirectory + "/audio.wav",
            new string('b', 64),
            2_000,
            "pcm_s16le",
            16_000,
            1,
            480_000,
            30_000_000,
            preparedDirectory + "/timestamp-map.json",
            new string('c', 64),
            300,
            preparedDirectory + "/preprocessing-manifest.json",
            new string('d', 64),
            500,
            0,
            30_000_000,
            1,
            1,
            "8.1.2",
            "gcc 15.2.0",
            new string('e', 64),
            new string('1', 64),
            new string('2', 64),
            MediaQualityState.NotAssessed,
            ModelApplicabilityState.NotAssessed,
            preprocessedAtUtc);
    }

    private static StoredMediaValidationResult ValidationResult(
        Guid mediaAssetId,
        DateTimeOffset validatedAtUtc)
    {
        var hash = new string('8', 64);
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
            30,
            1,
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

    private static async Task<(StoredProfile Profile, IReadOnlyList<StoredMediaAsset> Assets)>
        AddValidatedProfileAsync(SqliteProfileStore store, string name, int videoCount)
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

        var assetIds = Enumerable.Range(0, videoCount).Select(_ => Guid.NewGuid()).ToArray();
        var ingestJobId = Guid.NewGuid();
        await store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            ingestJobId,
            "Processing/ingest/job",
            videoCount,
            videoCount * 100L,
            BaseTime.AddMinutes(1));
        await store.CompleteLocalMediaIngestJobAsync(
            ingestJobId,
            videos.Select((video, index) => new MediaAssetRegistration(
                video.Id,
                assetIds[index],
                new string((char)('1' + index), 64),
                $"Media/asset_{assetIds[index]:N}/original.mp4",
                100)).ToArray(),
            BaseTime.AddMinutes(2));
        var ingested = (await store.GetByIdAsync(profile.Id))!;
        var validationJobId = Guid.NewGuid();
        await store.StartMediaValidationJobAsync(
            profile.Id,
            ingested.UpdatedAtUtc,
            validationJobId,
            "Processing/validation/job",
            BaseTime.AddMinutes(3));
        var assets = await store.GetMediaAssetsForValidationJobAsync(validationJobId);
        await store.CompleteMediaValidationJobAsync(
            validationJobId,
            assets.Select(asset => ValidationSuccess(
                ValidationResult(asset.Id, BaseTime.AddMinutes(4)))).ToArray(),
            BaseTime.AddMinutes(5));
        return ((await store.GetByIdAsync(profile.Id))!, await store.GetMediaAssetsAsync(profile.Id));
    }

    private static async Task DowngradeToVersionFourAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER audio_observation_results_immutable_update;
            DROP TABLE audio_observation_job_assets;
            DROP TABLE audio_observation_results;
            ALTER TABLE media_assets DROP COLUMN audio_observation_failure;
            DROP TRIGGER training_videos_dependency_group_profile_insert;
            DROP TRIGGER training_videos_dependency_group_profile_update;
            DROP INDEX ix_training_videos_recording_dependency_group;
            ALTER TABLE training_videos DROP COLUMN recording_dependency_group_id;
            DROP TABLE recording_dependency_groups;
            DROP TRIGGER media_preprocessing_results_immutable_update;
            DROP TABLE media_preprocessing_job_assets;
            DROP TABLE media_preprocessing_results;
            ALTER TABLE media_assets DROP COLUMN preprocessing_failure;
            PRAGMA user_version = 4;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(string databasePath, string tableName)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> TableHasColumnAsync(
        string databasePath,
        string tableName,
        string columnName)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
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
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }
}
