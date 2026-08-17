using Microsoft.Data.Sqlite;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileStoreMediaIngestTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 15, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VersionTwoDatabaseMigratesToCurrentVersionWithoutLosingProfiles()
    {
        using var database = new TestDatabase();
        var profileId = Guid.NewGuid();
        var videoId = Guid.NewGuid();
        await CreateVersionTwoDatabaseAsync(database.DatabasePath, profileId, videoId);

        var restartedStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await restartedStore.InitializeAsync();

        Assert.Equal(6L, await ReadSchemaVersionAsync(database.DatabasePath));
        Assert.True(await TableHasColumnAsync(database.DatabasePath, "training_videos", "media_asset_id"));
        Assert.True(await TableExistsAsync(database.DatabasePath, "media_assets"));
        Assert.True(await TableExistsAsync(database.DatabasePath, "processing_jobs"));
        var loaded = await restartedStore.GetByIdAsync(profileId);
        Assert.NotNull(loaded);
        Assert.Single(loaded.TrainingVideos);
        Assert.Equal(videoId, loaded.TrainingVideos[0].Id);
        Assert.Null(loaded.TrainingVideos[0].MediaAssetId);
    }

    [Fact]
    public async Task LocalIngestLifecyclePersistsProgressAssetsLinksAndReadinessAcrossRestart()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile(
            "Lifecycle",
            TrainingCondition.VerifiedSincereTruth,
            TrainingCondition.VerifiedIntentionalDeception);
        await database.Store.AddAsync(profile);
        var jobId = Guid.NewGuid();
        var startedAt = BaseTime.AddMinutes(1);

        var started = await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            @"processing\jobs\job-one",
            totalItemCount: 2,
            totalBytes: 30,
            startedAt);

        Assert.Equal(ProcessingJobState.Queued, started.State);
        Assert.Equal("Processing/jobs/job-one", started.WorkspaceRelativePath);
        Assert.Equal(ProfileReadiness.IngestingMedia.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
        Assert.True(await database.Store.UpdateProcessingJobProgressAsync(
            jobId,
            ProcessingJobState.Running,
            completedItemCount: 1,
            completedBytes: 10,
            BaseTime.AddMinutes(2)));

        var truthAssetId = Guid.NewGuid();
        var deceptionAssetId = Guid.NewGuid();
        var assets = await database.Store.CompleteLocalMediaIngestJobAsync(
            jobId,
            [
                Registration(profile.TrainingVideos[0].Id, truthAssetId, 'a', "media/truth.mp4", 10),
                Registration(profile.TrainingVideos[1].Id, deceptionAssetId, 'b', "media/deception.mp4", 20),
            ],
            BaseTime.AddMinutes(3));

        Assert.Equal(2, assets.Count);
        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var loaded = await restarted.GetByIdAsync(profile.Id);
        Assert.NotNull(loaded);
        Assert.Equal(ProfileReadiness.MediaIngestedAwaitingProbe.ToString(), loaded.Readiness);
        Assert.Equal(truthAssetId, loaded.TrainingVideos[0].MediaAssetId);
        Assert.Equal(deceptionAssetId, loaded.TrainingVideos[1].MediaAssetId);
        Assert.Equal(2, (await restarted.GetMediaAssetsAsync(profile.Id)).Count);
        var storedJob = Assert.Single(await restarted.GetProcessingJobsAsync(profile.Id));
        Assert.Equal(ProcessingJobState.Completed, storedJob.State);
        Assert.Equal(2, storedJob.CompletedItemCount);
        Assert.Equal(30, storedJob.CompletedBytes);
        Assert.Null(storedJob.Error);
    }

    [Fact]
    public async Task SameHashAndConditionDeduplicatesAndReturnsThePersistedAsset()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile(
            "Dedupe",
            TrainingCondition.VerifiedSincereTruth,
            TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var hash = new string('c', 64);
        var firstAssetId = Guid.NewGuid();

        var firstJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            firstJobId,
            "processing/first",
            1,
            12,
            BaseTime.AddMinutes(1));
        await database.Store.CompleteLocalMediaIngestJobAsync(
            firstJobId,
            [new MediaAssetRegistration(
                profile.TrainingVideos[0].Id,
                firstAssetId,
                hash,
                "media/original.mp4",
                12)],
            BaseTime.AddMinutes(2));
        var partlyIngested = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(partlyIngested);
        Assert.Equal(ProfileReadiness.Draft.ToString(), partlyIngested.Readiness);

        var secondJobId = Guid.NewGuid();
        var redundantAssetId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            partlyIngested.UpdatedAtUtc,
            secondJobId,
            "processing/second",
            1,
            12,
            BaseTime.AddMinutes(3));
        var returnedAssets = await database.Store.CompleteLocalMediaIngestJobAsync(
            secondJobId,
            [new MediaAssetRegistration(
                profile.TrainingVideos[1].Id,
                redundantAssetId,
                hash,
                "media/redundant-candidate.mp4",
                12)],
            BaseTime.AddMinutes(4));

        var persistedAsset = Assert.Single(returnedAssets);
        Assert.Equal(firstAssetId, persistedAsset.Id);
        Assert.Equal("Media/original.mp4", persistedAsset.WorkspaceRelativePath);
        var allAssets = await database.Store.GetMediaAssetsAsync(profile.Id);
        Assert.Single(allAssets);
        var completedProfile = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(completedProfile);
        Assert.All(completedProfile.TrainingVideos, video => Assert.Equal(firstAssetId, video.MediaAssetId));
        Assert.Equal(ProfileReadiness.MediaIngestedAwaitingProbe.ToString(), completedProfile.Readiness);
    }

    [Fact]
    public async Task SameHashAndConditionWithinOneJobCreatesOneAssetAndLinksBothVideos()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile(
            "One-job dedupe",
            TrainingCondition.VerifiedSincereTruth,
            TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var jobId = Guid.NewGuid();
        var firstCandidateId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "processing/one-job-dedupe",
            2,
            18,
            BaseTime.AddMinutes(1));

        var assets = await database.Store.CompleteLocalMediaIngestJobAsync(
            jobId,
            [
                Registration(
                    profile.TrainingVideos[0].Id,
                    firstCandidateId,
                    'e',
                    "media/first-candidate.mp4",
                    9),
                Registration(
                    profile.TrainingVideos[1].Id,
                    firstCandidateId,
                    'e',
                    "media/first-candidate.mp4",
                    9),
            ],
            BaseTime.AddMinutes(2));

        Assert.Equal(firstCandidateId, Assert.Single(assets).Id);
        Assert.Single(await database.Store.GetMediaAssetsAsync(profile.Id));
        var loaded = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(loaded);
        Assert.All(loaded.TrainingVideos, video => Assert.Equal(firstCandidateId, video.MediaAssetId));
    }

    [Fact]
    public async Task SameHashWithMultipleCandidateAssetIdsIsRejectedWithoutPartialRegistration()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile(
            "Conflicting candidate IDs",
            TrainingCondition.VerifiedSincereTruth,
            TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var jobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "Processing/conflicting-candidate-ids",
            2,
            18,
            BaseTime.AddMinutes(1));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CompleteLocalMediaIngestJobAsync(
                jobId,
                [
                    Registration(
                        profile.TrainingVideos[0].Id,
                        Guid.NewGuid(),
                        '6',
                        "Media/shared-candidate/original.mp4",
                        9),
                    Registration(
                        profile.TrainingVideos[1].Id,
                        Guid.NewGuid(),
                        '6',
                        "Media/shared-candidate/original.mp4",
                        9),
                ],
                BaseTime.AddMinutes(2)));

        Assert.Contains("exactly one media asset ID", exception.Message);
        await AssertCompletionWasRolledBackAsync(database.Store, profile.Id, jobId);
    }

    [Fact]
    public async Task SameHashWithMultipleCanonicalCandidatePathsIsRejectedWithoutPartialRegistration()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile(
            "Conflicting candidate paths",
            TrainingCondition.VerifiedSincereTruth,
            TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "Processing/conflicting-candidate-paths",
            2,
            18,
            BaseTime.AddMinutes(1));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CompleteLocalMediaIngestJobAsync(
                jobId,
                [
                    Registration(
                        profile.TrainingVideos[0].Id,
                        assetId,
                        '7',
                        "Media/first-candidate/original.mp4",
                        9),
                    Registration(
                        profile.TrainingVideos[1].Id,
                        assetId,
                        '7',
                        "Media/second-candidate/original.mp4",
                        9),
                ],
                BaseTime.AddMinutes(2)));

        Assert.Contains("one canonical workspace-relative path", exception.Message);
        await AssertCompletionWasRolledBackAsync(database.Store, profile.Id, jobId);
    }

    [Fact]
    public async Task DifferentContentCannotReuseAnExistingProfileMediaPath()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile(
            "Path collision",
            TrainingCondition.VerifiedSincereTruth,
            TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var firstJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            firstJobId,
            "Processing/first-path-owner",
            1,
            4,
            BaseTime.AddMinutes(1));
        await database.Store.CompleteLocalMediaIngestJobAsync(
            firstJobId,
            [Registration(
                profile.TrainingVideos[0].Id,
                Guid.NewGuid(),
                '1',
                "Media/shared/original.mp4",
                4)],
            BaseTime.AddMinutes(2));
        var partlyIngested = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(partlyIngested);

        var secondJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            partlyIngested.UpdatedAtUtc,
            secondJobId,
            "Processing/second-path-owner",
            1,
            4,
            BaseTime.AddMinutes(3));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            database.Store.CompleteLocalMediaIngestJobAsync(
                secondJobId,
                [Registration(
                    profile.TrainingVideos[1].Id,
                    Guid.NewGuid(),
                    '2',
                    "media/SHARED/original.mp4",
                    4)],
                BaseTime.AddMinutes(4)));

        Assert.Single(await database.Store.GetMediaAssetsAsync(profile.Id));
        var unchanged = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(unchanged);
        Assert.Null(unchanged.TrainingVideos[1].MediaAssetId);
        Assert.Equal(ProcessingJobState.Queued,
            (await database.Store.GetProcessingJobAsync(secondJobId))!.State);
    }

    [Fact]
    public async Task CompleteRejectsIdenticalContentAcrossConditionsAndRollsBackRegistrations()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile(
            "Condition conflict",
            TrainingCondition.VerifiedSincereTruth,
            TrainingCondition.VerifiedIntentionalDeception);
        await database.Store.AddAsync(profile);
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "processing/conflict",
            2,
            16,
            BaseTime.AddMinutes(1));

        var exception = await Assert.ThrowsAsync<MediaAssetConditionConflictException>(() =>
            database.Store.CompleteLocalMediaIngestJobAsync(
                jobId,
                [
                    Registration(profile.TrainingVideos[0].Id, assetId, 'd', "media/same.mp4", 8),
                    Registration(profile.TrainingVideos[1].Id, assetId, 'd', "media/same.mp4", 8),
                ],
                BaseTime.AddMinutes(2)));

        Assert.Equal(new string('d', 64), exception.Sha256);
        Assert.Empty(await database.Store.GetMediaAssetsAsync(profile.Id));
        var unchanged = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(unchanged);
        Assert.All(unchanged.TrainingVideos, video => Assert.Null(video.MediaAssetId));
        Assert.Equal(ProfileReadiness.IngestingMedia.ToString(), unchanged.Readiness);
        Assert.Equal(ProcessingJobState.Queued,
            (await database.Store.GetProcessingJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task CompleteCannotLinkAnArchivedOrAlreadyLinkedTrainingVideo()
    {
        using var database = new TestDatabase();
        var archivedProfile = CreateProfile(
            "Archived registration",
            TrainingCondition.VerifiedSincereTruth) with
        {
            TrainingVideos =
            [
                CreateProfile("unused", TrainingCondition.VerifiedSincereTruth)
                    .TrainingVideos[0] with { IsArchived = true },
            ],
        };
        await database.Store.AddAsync(archivedProfile);
        var archivedJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            archivedProfile.Id,
            archivedProfile.UpdatedAtUtc,
            archivedJobId,
            "Processing/archived",
            1,
            2,
            BaseTime.AddMinutes(1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Store.CompleteLocalMediaIngestJobAsync(
                archivedJobId,
                [Registration(
                    archivedProfile.TrainingVideos[0].Id,
                    Guid.NewGuid(),
                    '3',
                    "Media/archived/original.mp4",
                    2)],
                BaseTime.AddMinutes(2)));

        var linkedProfile = CreateProfile("Already linked", TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(linkedProfile);
        var firstJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            linkedProfile.Id,
            linkedProfile.UpdatedAtUtc,
            firstJobId,
            "Processing/first-link",
            1,
            2,
            BaseTime.AddMinutes(1));
        await database.Store.CompleteLocalMediaIngestJobAsync(
            firstJobId,
            [Registration(
                linkedProfile.TrainingVideos[0].Id,
                Guid.NewGuid(),
                '4',
                "Media/first-link/original.mp4",
                2)],
            BaseTime.AddMinutes(2));
        var linked = await database.Store.GetByIdAsync(linkedProfile.Id);
        Assert.NotNull(linked);
        var originalAssetId = linked.TrainingVideos[0].MediaAssetId;

        var relinkJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            linkedProfile.Id,
            linked.UpdatedAtUtc,
            relinkJobId,
            "Processing/relink",
            1,
            2,
            BaseTime.AddMinutes(3));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Store.CompleteLocalMediaIngestJobAsync(
                relinkJobId,
                [Registration(
                    linkedProfile.TrainingVideos[0].Id,
                    Guid.NewGuid(),
                    '5',
                    "Media/relink/original.mp4",
                    2)],
                BaseTime.AddMinutes(4)));
        Assert.Equal(originalAssetId,
            (await database.Store.GetByIdAsync(linkedProfile.Id))!.TrainingVideos[0].MediaAssetId);
    }

    [Fact]
    public async Task StartUsesOptimisticConcurrencyAndEditRejectsAnActiveJob()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Concurrency", TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);

        await Assert.ThrowsAsync<ProfileConcurrencyConflictException>(() =>
            database.Store.StartLocalMediaIngestJobAsync(
                profile.Id,
                profile.UpdatedAtUtc.AddSeconds(-1),
                Guid.NewGuid(),
                "processing/stale",
                1,
                10,
                BaseTime.AddMinutes(1)));
        Assert.Empty(await database.Store.GetProcessingJobsAsync(profile.Id));

        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            Guid.NewGuid(),
            "processing/active",
            1,
            10,
            BaseTime.AddMinutes(1));
        await Assert.ThrowsAsync<ProfileProcessingActiveException>(() =>
            database.Store.UpdateAsync(
                profile with
                {
                    DisplayName = "Must not be saved",
                    UpdatedAtUtc = BaseTime.AddMinutes(2),
                },
                BaseTime.AddMinutes(1)));

        Assert.Equal("Concurrency", (await database.Store.GetByIdAsync(profile.Id))!.DisplayName);
    }

    [Fact]
    public async Task TwoStoreInstancesCannotStartTwoActiveJobsForOneProfile()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Two-store start", TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var firstStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var secondStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);

        async Task<Exception?> TryStartAsync(SqliteProfileStore store, string suffix)
        {
            try
            {
                await store.StartLocalMediaIngestJobAsync(
                    profile.Id,
                    profile.UpdatedAtUtc,
                    Guid.NewGuid(),
                    $"Processing/concurrent-{suffix}",
                    1,
                    4,
                    BaseTime.AddMinutes(1));
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var outcomes = await Task.WhenAll(
            TryStartAsync(firstStore, "first"),
            TryStartAsync(secondStore, "second"));

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome is ProfileProcessingActiveException);
        Assert.Single(
            await firstStore.GetProcessingJobsAsync(profile.Id),
            job => job.State is ProcessingJobState.Queued or ProcessingJobState.Running);
    }

    [Fact]
    public async Task RecoveryAndHeartbeatRaceLeavesOneConsistentOutcome()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Recovery race", TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var jobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "Processing/recovery-race",
            1,
            4,
            BaseTime.AddMinutes(1));
        var heartbeatStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var recoveryStore = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);

        var heartbeatTask = heartbeatStore.UpdateProcessingJobProgressAsync(
            jobId,
            ProcessingJobState.Running,
            0,
            0,
            BaseTime.AddMinutes(20));
        var recoveryTask = recoveryStore.RecoverInterruptedJobsAsync(
            BaseTime.AddMinutes(10),
            BaseTime.AddMinutes(30));
        await Task.WhenAll(heartbeatTask, recoveryTask);
        var heartbeatUpdated = await heartbeatTask;
        var recoveredCount = await recoveryTask;

        var job = await database.Store.GetProcessingJobAsync(jobId);
        var storedProfile = await database.Store.GetByIdAsync(profile.Id);
        Assert.NotNull(job);
        Assert.NotNull(storedProfile);
        if (heartbeatUpdated)
        {
            Assert.Equal(0, recoveredCount);
            Assert.Equal(ProcessingJobState.Running, job.State);
            Assert.Equal(ProfileReadiness.IngestingMedia.ToString(), storedProfile.Readiness);
        }
        else
        {
            Assert.Equal(1, recoveredCount);
            Assert.Equal(ProcessingJobState.Interrupted, job.State);
            Assert.Equal(ProfileReadiness.Draft.ToString(), storedProfile.Readiness);
        }
    }

    [Fact]
    public async Task FailedJobSanitizesErrorRestoresDraftAndIgnoresLateProgress()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Failure", TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var jobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "processing/failure",
            1,
            10,
            BaseTime.AddMinutes(1));
        Assert.True(await database.Store.TerminateProcessingJobAsync(
            jobId,
            ProcessingJobState.Failed,
            "copy\r\nfailed\0",
            BaseTime.AddMinutes(2)));

        var failed = await database.Store.GetProcessingJobAsync(jobId);
        Assert.NotNull(failed);
        Assert.Equal(ProcessingJobState.Failed, failed.State);
        Assert.NotNull(failed.Error);
        Assert.DoesNotContain(failed.Error, char.IsControl);
        Assert.Equal(ProfileReadiness.Draft.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
        Assert.False(await database.Store.UpdateProcessingJobProgressAsync(
            jobId,
            ProcessingJobState.Running,
            1,
            10,
            BaseTime.AddMinutes(3)));
        Assert.False(await database.Store.TerminateProcessingJobAsync(
            jobId,
            ProcessingJobState.Cancelled,
            null,
            BaseTime.AddMinutes(4)));
    }

    [Fact]
    public async Task RestartRecoveryInterruptsOnlyJobsOlderThanTheStaleThreshold()
    {
        using var database = new TestDatabase();
        var oldProfile = CreateProfile("Old job", TrainingCondition.VerifiedSincereTruth);
        var freshProfile = CreateProfile("Fresh job", TrainingCondition.VerifiedIntentionalDeception);
        await database.Store.AddAsync(oldProfile);
        await database.Store.AddAsync(freshProfile);
        var oldJobId = Guid.NewGuid();
        var freshJobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            oldProfile.Id,
            oldProfile.UpdatedAtUtc,
            oldJobId,
            "processing/old",
            1,
            5,
            BaseTime.AddMinutes(1));
        await database.Store.StartLocalMediaIngestJobAsync(
            freshProfile.Id,
            freshProfile.UpdatedAtUtc,
            freshJobId,
            "processing/fresh",
            1,
            7,
            BaseTime.AddMinutes(20));

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var recovered = await restarted.RecoverInterruptedJobsAsync(
            staleBeforeUtc: BaseTime.AddMinutes(10),
            recoveredAtUtc: BaseTime.AddMinutes(30));

        Assert.Equal(1, recovered);
        Assert.Equal(ProcessingJobState.Interrupted,
            (await restarted.GetProcessingJobAsync(oldJobId))!.State);
        Assert.Equal(ProcessingJobState.Queued,
            (await restarted.GetProcessingJobAsync(freshJobId))!.State);
        Assert.Equal(ProfileReadiness.Draft.ToString(),
            (await restarted.GetByIdAsync(oldProfile.Id))!.Readiness);
        Assert.Equal(ProfileReadiness.IngestingMedia.ToString(),
            (await restarted.GetByIdAsync(freshProfile.Id))!.Readiness);
    }

    [Fact]
    public async Task InputValidationRejectsTraversalInvalidHashAndInvalidCounters()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Validation", TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.StartLocalMediaIngestJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                Guid.NewGuid(),
                "../outside",
                1,
                1,
                BaseTime.AddMinutes(1)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.StartLocalMediaIngestJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                Guid.NewGuid(),
                "Media/not-a-job",
                1,
                1,
                BaseTime.AddMinutes(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            database.Store.StartLocalMediaIngestJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                Guid.NewGuid(),
                "processing/invalid-count",
                -1,
                1,
                BaseTime.AddMinutes(1)));

        var jobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "processing/validation",
            1,
            1,
            BaseTime.AddMinutes(1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CompleteLocalMediaIngestJobAsync(
                jobId,
                [new MediaAssetRegistration(
                    profile.TrainingVideos[0].Id,
                    Guid.NewGuid(),
                    new string('A', 64),
                    "media/file.mp4",
                    1)],
                BaseTime.AddMinutes(2)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CompleteLocalMediaIngestJobAsync(
                jobId,
                [new MediaAssetRegistration(
                    profile.TrainingVideos[0].Id,
                    Guid.NewGuid(),
                    new string('a', 64),
                    "Processing/not-an-asset.mp4",
                    1)],
                BaseTime.AddMinutes(2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            database.Store.UpdateProcessingJobProgressAsync(
                jobId,
                ProcessingJobState.Running,
                2,
                1,
                BaseTime.AddMinutes(2)));
    }

    [Fact]
    public async Task LoadRejectsStoredJobOrAssetPathsOutsideTheirBoundedRoots()
    {
        using var database = new TestDatabase();
        var profile = CreateProfile("Corrupt bounded paths", TrainingCondition.VerifiedSincereTruth);
        await database.Store.AddAsync(profile);
        var jobId = Guid.NewGuid();
        await database.Store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "Processing/corrupt-path-test",
            1,
            3,
            BaseTime.AddMinutes(1));
        await database.Store.CompleteLocalMediaIngestJobAsync(
            jobId,
            [Registration(
                profile.TrainingVideos[0].Id,
                Guid.NewGuid(),
                'f',
                "Media/corrupt-path-test/original.mp4",
                3)],
            BaseTime.AddMinutes(2));

        await ExecuteAsync(
            database.DatabasePath,
            "UPDATE processing_jobs SET workspace_relative_path = 'Media/not-a-job' WHERE id = $id;",
            jobId);
        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.GetProcessingJobAsync(jobId));

        await ExecuteAsync(
            database.DatabasePath,
            "UPDATE media_assets SET workspace_relative_path = 'Processing/not-media.mp4' WHERE profile_id = $id;",
            profile.Id);
        restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.GetMediaAssetsAsync(profile.Id));
    }

    private static StoredProfile CreateProfile(
        string name,
        params TrainingCondition[] conditions)
    {
        return new StoredProfile(
            Guid.NewGuid(),
            name,
            $@"D:\profiles\{name.Replace(' ', '-')}",
            @"D:\profiles\downloads",
            ProfileReadiness.Draft.ToString(),
            BaseTime.AddMinutes(-10),
            BaseTime,
            conditions.Select((condition, index) => new StoredTrainingVideo(
                Guid.NewGuid(),
                $@"D:\media\video-{index}.mp4",
                $"recording-{index}",
                condition,
                IsArchived: false,
                SortOrder: index)).ToArray());
    }

    private static MediaAssetRegistration Registration(
        Guid trainingVideoId,
        Guid mediaAssetId,
        char hashCharacter,
        string relativePath,
        long byteLength)
    {
        return new MediaAssetRegistration(
            trainingVideoId,
            mediaAssetId,
            new string(hashCharacter, 64),
            relativePath,
            byteLength);
    }

    private static async Task AssertCompletionWasRolledBackAsync(
        SqliteProfileStore store,
        Guid profileId,
        Guid jobId)
    {
        Assert.Empty(await store.GetMediaAssetsAsync(profileId));
        var profile = await store.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        Assert.All(profile.TrainingVideos, video => Assert.Null(video.MediaAssetId));
        Assert.Equal(ProfileReadiness.IngestingMedia.ToString(), profile.Readiness);
        Assert.Equal(ProcessingJobState.Queued,
            (await store.GetProcessingJobAsync(jobId))!.State);
    }

    private static async Task CreateVersionTwoDatabaseAsync(
        string databasePath,
        Guid profileId,
        Guid videoId)
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
            CREATE UNIQUE INDEX ux_profiles_workspace_root_nocase
                ON profiles(workspace_root COLLATE NOCASE);

            INSERT INTO profiles VALUES (
                $profileId, 'Migrated', 'D:\profiles\migrated', NULL, 'Draft', $createdUtc, $updatedUtc);
            INSERT INTO training_videos VALUES (
                $videoId, $profileId, 'D:\media\old.mp4', 'old', 'VerifiedSincereTruth', 0, 0);
            PRAGMA user_version = 2;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        command.Parameters.AddWithValue("$videoId", videoId.ToString("D"));
        command.Parameters.AddWithValue("$createdUtc", BaseTime.AddMinutes(-5).ToString("O"));
        command.Parameters.AddWithValue("$updatedUtc", BaseTime.ToString("O"));
        await command.ExecuteNonQueryAsync();
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
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;";
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
