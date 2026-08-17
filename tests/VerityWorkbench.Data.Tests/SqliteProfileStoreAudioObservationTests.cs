using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileStoreAudioObservationTests
{
    private const string ObservationContractVersion = "verityworkbench.audio-pcm-observation.v1";
    private static readonly string ObservationContractSha256 = new('9', 64);
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 17, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VersionSixDatabaseMigratesWithoutChangingPreparedMedia()
    {
        using var database = new TestDatabase();
        var setup = await AddPreparedProfileAsync(database.Store, "Version six migration", 1);
        await DowngradeToVersionSixAsync(database.DatabasePath);

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await restarted.InitializeAsync();

        Assert.Equal(8L, await ReadSchemaVersionAsync(database.DatabasePath));
        Assert.True(await TableHasColumnAsync(
            database.DatabasePath,
            "media_assets",
            "audio_observation_failure"));
        Assert.True(await TableExistsAsync(database.DatabasePath, "audio_observation_results"));
        Assert.True(await TableExistsAsync(database.DatabasePath, "audio_observation_job_assets"));
        var asset = Assert.Single(await restarted.GetMediaAssetsAsync(setup.Profile.Id));
        Assert.Equal(MediaAssetState.Prepared, asset.State);
        Assert.Null(asset.AudioObservationFailure);
        Assert.NotNull(await restarted.GetMediaPreprocessingResultAsync(asset.Id));
        Assert.Null(await restarted.GetAudioObservationResultAsync(asset.Id));
        Assert.Equal(
            ProfileReadiness.MediaPrepared.ToString(),
            (await restarted.GetByIdAsync(setup.Profile.Id))!.Readiness);
    }

    [Fact]
    public async Task SuccessfulLifecyclePersistsImmutableLabelBlindFacts()
    {
        using var database = new TestDatabase();
        var setup = await AddPreparedProfileAsync(database.Store, "Audio observation lifecycle", 1);
        var jobId = Guid.NewGuid();
        var job = await database.Store.StartAudioObservationJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            jobId,
            "Processing/audio-observation/job-one",
            ObservationContractVersion,
            ObservationContractSha256,
            BaseTime.AddMinutes(9));

        Assert.Equal(ProcessingJobKind.AudioObservationExtraction, job.Kind);
        Assert.Equal(1, job.TotalItemCount);
        Assert.Equal(2_000, job.TotalBytes);
        Assert.Equal(
            ProfileReadiness.ExtractingAudioObservations.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
        var snapshot = Assert.Single(await database.Store.GetAudioObservationJobAssetsAsync(jobId));
        var prepared = Assert.Single(setup.PreparedResults);
        Assert.Equal(prepared.MediaAssetId, snapshot.MediaAssetId);
        Assert.Equal(prepared.AnalysisAudioWorkspaceRelativePath, snapshot.AnalysisAudioWorkspaceRelativePath);
        Assert.Equal(prepared.AnalysisAudioSha256, snapshot.AnalysisAudioSha256);
        Assert.Equal(prepared.AnalysisAudioByteLength, snapshot.AnalysisAudioByteLength);
        Assert.Equal(prepared.AnalysisAudioSampleCount, snapshot.AnalysisAudioSampleCount);
        Assert.Equal(prepared.PreprocessingContractSha256, snapshot.PreprocessingContractSha256);
        Assert.Equal(ObservationContractVersion, snapshot.ObservationContractVersion);
        Assert.Equal(ObservationContractSha256, snapshot.ObservationContractSha256);
        var result = ObservationResult(snapshot, BaseTime.AddMinutes(10));

        await database.Store.CompleteAudioObservationJobAsync(
            jobId,
            [new AudioObservationRegistration(snapshot.MediaAssetId, result, null)],
            BaseTime.AddMinutes(11));

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var observedAsset = Assert.Single(await restarted.GetMediaAssetsAsync(setup.Profile.Id));
        Assert.Equal(MediaAssetState.Prepared, observedAsset.State);
        Assert.Null(observedAsset.AudioObservationFailure);
        Assert.Equal(
            ProfileReadiness.AudioObserved.ToString(),
            (await restarted.GetByIdAsync(setup.Profile.Id))!.Readiness);
        Assert.Equal(result, await restarted.GetAudioObservationResultAsync(snapshot.MediaAssetId));
        Assert.Equal(result, Assert.Single(
            await restarted.GetAudioObservationResultsAsync(setup.Profile.Id)));
        Assert.Equal(MediaQualityState.NotAssessed, result.MediaQualityState);
        Assert.Equal(ModelApplicabilityState.NotAssessed, result.ModelApplicabilityState);
        var completedJob = (await restarted.GetProcessingJobAsync(jobId))!;
        Assert.Equal(ProcessingJobState.Completed, completedJob.State);
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            database.DatabasePath,
            "UPDATE audio_observation_results SET exact_sample_sum = '2' " +
            "WHERE media_asset_id = $id;",
            snapshot.MediaAssetId));

        var profile = (await restarted.GetByIdAsync(setup.Profile.Id))!;
        var validationBeforeCleanup = await restarted.GetMediaValidationResultAsync(snapshot.MediaAssetId);
        var preprocessingBeforeCleanup = await restarted.GetMediaPreprocessingResultAsync(snapshot.MediaAssetId);
        Assert.True(await restarted.MarkProcessingJobWorkspaceCleanedAsync(
            profile.Id,
            jobId,
            ProcessingJobState.Completed,
            completedJob.WorkspaceRelativePath,
            BaseTime.AddMinutes(12)));
        var afterCleanup = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var profileAfterCleanup = await afterCleanup.GetByIdAsync(profile.Id);
        Assert.NotNull(profileAfterCleanup);
        Assert.Equal(profile.Readiness, profileAfterCleanup.Readiness);
        Assert.Equal(profile.UpdatedAtUtc, profileAfterCleanup.UpdatedAtUtc);
        Assert.Equal(profile.TrainingVideos.ToArray(), profileAfterCleanup.TrainingVideos.ToArray());
        Assert.Equal(observedAsset, Assert.Single(await afterCleanup.GetMediaAssetsAsync(profile.Id)));
        Assert.Equal(validationBeforeCleanup, await afterCleanup.GetMediaValidationResultAsync(snapshot.MediaAssetId));
        Assert.Equal(
            preprocessingBeforeCleanup,
            await afterCleanup.GetMediaPreprocessingResultAsync(snapshot.MediaAssetId));
        Assert.Equal(result, await afterCleanup.GetAudioObservationResultAsync(snapshot.MediaAssetId));
        Assert.Equal(
            BaseTime.AddMinutes(12),
            (await afterCleanup.GetProcessingJobAsync(jobId))!.WorkspaceCleanedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            afterCleanup.StartAudioObservationJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                Guid.NewGuid(),
                "Processing/audio-observation/no-pending-assets",
                ObservationContractVersion,
                ObservationContractSha256,
                BaseTime.AddMinutes(13)));
    }

    [Fact]
    public async Task StartDeduplicatesSharedActiveAssetAndExcludesArchivedOnlyAsset()
    {
        using var database = new TestDatabase();
        var setup = await AddPreparedProfileAsync(database.Store, "Audio observation selection scope", 3);
        var preparedProfile = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        var sharedAssetId = preparedProfile.TrainingVideos[0].MediaAssetId!.Value;
        var archivedOnlyAssetId = preparedProfile.TrainingVideos[1].MediaAssetId!.Value;
        var scopedProfile = preparedProfile with
        {
            UpdatedAtUtc = BaseTime.AddMinutes(9),
            TrainingVideos = preparedProfile.TrainingVideos
                .Select((video, index) => index switch
                {
                    1 => video with { IsArchived = true },
                    2 => video with { MediaAssetId = sharedAssetId },
                    _ => video,
                })
                .ToArray(),
        };
        await database.Store.UpdateAsync(scopedProfile, preparedProfile.UpdatedAtUtc);
        var savedProfile = (await database.Store.GetByIdAsync(setup.Profile.Id))!;

        var jobId = Guid.NewGuid();
        var job = await database.Store.StartAudioObservationJobAsync(
            savedProfile.Id,
            savedProfile.UpdatedAtUtc,
            jobId,
            "Processing/audio-observation/selection-scope",
            ObservationContractVersion,
            ObservationContractSha256,
            BaseTime.AddMinutes(10));

        Assert.Equal(1, job.TotalItemCount);
        Assert.Equal(2_000, job.TotalBytes);
        var snapshot = Assert.Single(await database.Store.GetAudioObservationJobAssetsAsync(jobId));
        Assert.Equal(sharedAssetId, snapshot.MediaAssetId);
        Assert.NotEqual(archivedOnlyAssetId, snapshot.MediaAssetId);
        var result = ObservationResult(snapshot, BaseTime.AddMinutes(11));

        await database.Store.CompleteAudioObservationJobAsync(
            jobId,
            [new AudioObservationRegistration(snapshot.MediaAssetId, result, null)],
            BaseTime.AddMinutes(12));

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        Assert.Equal(result, await restarted.GetAudioObservationResultAsync(sharedAssetId));
        Assert.Null(await restarted.GetAudioObservationResultAsync(archivedOnlyAssetId));
        Assert.Equal(result, Assert.Single(
            await restarted.GetAudioObservationResultsAsync(setup.Profile.Id)));
        Assert.Equal(
            ProfileReadiness.AudioObserved.ToString(),
            (await restarted.GetByIdAsync(setup.Profile.Id))!.Readiness);
    }

    [Fact]
    public async Task FailedItemIsSanitizedAndRetryClearsFailureWithoutChangingPreparedState()
    {
        using var database = new TestDatabase();
        var setup = await AddPreparedProfileAsync(database.Store, "Audio observation retry", 1);
        var firstJobId = Guid.NewGuid();
        await database.Store.StartAudioObservationJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            firstJobId,
            "Processing/audio-observation/first",
            ObservationContractVersion,
            ObservationContractSha256,
            BaseTime.AddMinutes(9));
        var snapshot = Assert.Single(await database.Store.GetAudioObservationJobAssetsAsync(firstJobId));

        await database.Store.CompleteAudioObservationJobAsync(
            firstJobId,
            [new AudioObservationRegistration(
                snapshot.MediaAssetId,
                null,
                "D:\\private\\audio.wav\n{\"tool\":\"raw output\"}")],
            BaseTime.AddMinutes(10));

        var failed = Assert.Single(await database.Store.GetMediaAssetsAsync(setup.Profile.Id));
        Assert.Equal(MediaAssetState.Prepared, failed.State);
        Assert.Equal(
            "Audio observation extraction failed; detailed tool output was not retained.",
            failed.AudioObservationFailure);
        Assert.Equal(
            ProfileReadiness.AudioObservationFailed.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
        Assert.Null(await database.Store.GetAudioObservationResultAsync(snapshot.MediaAssetId));
        Assert.DoesNotContain(
            "private",
            System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(database.DatabasePath)));

        var failedProfile = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        var retryJobId = Guid.NewGuid();
        var retryJob = await database.Store.StartAudioObservationJobAsync(
            failedProfile.Id,
            failedProfile.UpdatedAtUtc,
            retryJobId,
            "Processing/audio-observation/retry",
            ObservationContractVersion,
            ObservationContractSha256,
            BaseTime.AddMinutes(11));
        Assert.Equal(1, retryJob.TotalItemCount);
        var retrySnapshot = Assert.Single(
            await database.Store.GetAudioObservationJobAssetsAsync(retryJobId));
        await database.Store.CompleteAudioObservationJobAsync(
            retryJobId,
            [new AudioObservationRegistration(
                retrySnapshot.MediaAssetId,
                ObservationResult(retrySnapshot, BaseTime.AddMinutes(12)),
                null)],
            BaseTime.AddMinutes(13));

        var observed = Assert.Single(await database.Store.GetMediaAssetsAsync(setup.Profile.Id));
        Assert.Equal(MediaAssetState.Prepared, observed.State);
        Assert.Null(observed.AudioObservationFailure);
        Assert.Equal(
            ProfileReadiness.AudioObserved.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
    }

    [Fact]
    public async Task InvalidBatchIsAtomicAndCancellationRestoresDerivedReadiness()
    {
        using var database = new TestDatabase();
        var setup = await AddPreparedProfileAsync(database.Store, "Audio observation atomic", 2);
        var jobId = Guid.NewGuid();
        await database.Store.StartAudioObservationJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            jobId,
            "Processing/audio-observation/atomic",
            ObservationContractVersion,
            ObservationContractSha256,
            BaseTime.AddMinutes(9));
        var snapshots = await database.Store.GetAudioObservationJobAssetsAsync(jobId);
        Assert.Equal(2, snapshots.Count);
        var first = ObservationResult(snapshots[0], BaseTime.AddMinutes(10));
        var invalidSecond = ObservationResult(snapshots[1], BaseTime.AddMinutes(10)) with
        {
            ExactSampleSum = "+1",
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CompleteAudioObservationJobAsync(
                jobId,
                [
                    new AudioObservationRegistration(first.MediaAssetId, first, null),
                    new AudioObservationRegistration(invalidSecond.MediaAssetId, invalidSecond, null),
                ],
                BaseTime.AddMinutes(11)));

        Assert.Empty(await database.Store.GetAudioObservationResultsAsync(setup.Profile.Id));
        Assert.All(
            await database.Store.GetMediaAssetsAsync(setup.Profile.Id),
            asset =>
            {
                Assert.Equal(MediaAssetState.Prepared, asset.State);
                Assert.Null(asset.AudioObservationFailure);
            });
        Assert.Equal(
            ProcessingJobState.Queued,
            (await database.Store.GetProcessingJobAsync(jobId))!.State);
        Assert.True(await database.Store.TerminateProcessingJobAsync(
            jobId,
            ProcessingJobState.Cancelled,
            null,
            BaseTime.AddMinutes(12)));
        Assert.Equal(
            ProfileReadiness.MediaPrepared.ToString(),
            (await database.Store.GetByIdAsync(setup.Profile.Id))!.Readiness);
    }

    [Fact]
    public async Task InterruptedJobRestoresPreparedReadinessAndCanBeRetried()
    {
        using var database = new TestDatabase();
        var setup = await AddPreparedProfileAsync(database.Store, "Audio observation recovery", 1);
        var interruptedJobId = Guid.NewGuid();
        await database.Store.StartAudioObservationJobAsync(
            setup.Profile.Id,
            setup.Profile.UpdatedAtUtc,
            interruptedJobId,
            "Processing/audio-observation/interrupted",
            ObservationContractVersion,
            ObservationContractSha256,
            BaseTime.AddMinutes(9));

        Assert.Equal(1, await database.Store.RecoverInterruptedJobsAsync(
            BaseTime.AddMinutes(10),
            BaseTime.AddMinutes(11)));
        Assert.Equal(
            ProcessingJobState.Interrupted,
            (await database.Store.GetProcessingJobAsync(interruptedJobId))!.State);
        var recovered = (await database.Store.GetByIdAsync(setup.Profile.Id))!;
        Assert.Equal(ProfileReadiness.MediaPrepared.ToString(), recovered.Readiness);

        var retry = await database.Store.StartAudioObservationJobAsync(
            recovered.Id,
            recovered.UpdatedAtUtc,
            Guid.NewGuid(),
            "Processing/audio-observation/recovered-retry",
            ObservationContractVersion,
            ObservationContractSha256,
            BaseTime.AddMinutes(12));
        Assert.Equal(ProcessingJobKind.AudioObservationExtraction, retry.Kind);
        Assert.Equal(1, retry.TotalItemCount);
    }

    private static StoredAudioObservationResult ObservationResult(
        StoredAudioObservationJobAsset snapshot,
        DateTimeOffset observedAtUtc) =>
        new(
            snapshot.MediaAssetId,
            snapshot.AnalysisAudioSha256,
            snapshot.AnalysisAudioByteLength,
            snapshot.AnalysisAudioSampleRateHz,
            snapshot.AnalysisAudioChannelCount,
            snapshot.AnalysisAudioSampleCount,
            snapshot.AnalysisAudioDurationMicroseconds,
            snapshot.PreprocessingContractSha256,
            snapshot.ObservationContractVersion,
            snapshot.ObservationContractSha256,
            MinimumSignedSample: 0,
            MaximumSignedSample: 1,
            AbsolutePeakSample: 1,
            PositiveSampleCount: 1,
            NegativeSampleCount: 0,
            ZeroSampleCount: snapshot.AnalysisAudioSampleCount - 1,
            PositiveFullScaleSampleCount: 0,
            NegativeFullScaleSampleCount: 0,
            AdjacentOppositeSignCrossingCount: 0,
            ExactSampleSum: "1",
            ExactSquaredSampleSum: "1",
            MediaQualityState.NotAssessed,
            ModelApplicabilityState.NotAssessed,
            observedAtUtc);

    private static async Task<(
        StoredProfile Profile,
        IReadOnlyList<StoredMediaAsset> Assets,
        IReadOnlyList<StoredMediaPreprocessingResult> PreparedResults)> AddPreparedProfileAsync(
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
        var validatingAssets = await store.GetMediaAssetsForValidationJobAsync(validationJobId);
        await store.CompleteMediaValidationJobAsync(
            validationJobId,
            validatingAssets.Select(asset => new MediaValidationRegistration(
                asset.Id,
                MediaAssetState.Validated,
                ValidationResult(asset.Id, BaseTime.AddMinutes(4)),
                null)).ToArray(),
            BaseTime.AddMinutes(5));
        var validated = (await store.GetByIdAsync(profile.Id))!;
        var preprocessingJobId = Guid.NewGuid();
        await store.StartMediaPreprocessingJobAsync(
            profile.Id,
            validated.UpdatedAtUtc,
            preprocessingJobId,
            "Processing/preprocessing/job",
            BaseTime.AddMinutes(6));
        var preprocessingAssets = await store.GetMediaAssetsForPreprocessingJobAsync(preprocessingJobId);
        var preparedResults = preprocessingAssets
            .Select(asset => PreprocessingResult(asset, BaseTime.AddMinutes(7)))
            .ToArray();
        await store.CompleteMediaPreprocessingJobAsync(
            preprocessingJobId,
            preparedResults.Select(result => new MediaPreprocessingRegistration(
                result.MediaAssetId,
                MediaAssetState.Prepared,
                result,
                null)).ToArray(),
            BaseTime.AddMinutes(8));
        return (
            (await store.GetByIdAsync(profile.Id))!,
            await store.GetMediaAssetsAsync(profile.Id),
            preparedResults);
    }

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

    private static async Task DowngradeToVersionSixAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER audio_observation_results_immutable_update;
            DROP TABLE audio_observation_job_assets;
            DROP TABLE audio_observation_results;
            ALTER TABLE media_assets DROP COLUMN audio_observation_failure;
            ALTER TABLE processing_jobs DROP COLUMN workspace_cleaned_utc;
            PRAGMA user_version = 6;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string databasePath, string sql, Guid id)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadSchemaVersionAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(string databasePath, string tableName)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
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

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }
}
