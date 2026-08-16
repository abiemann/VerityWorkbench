using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.Data.Tests;

public sealed class SqliteProfileStoreMediaIntegrityTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 8, 15, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidatedAssetMutationPersistsAcrossRestartWithoutDeletingResult()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Integrity persistence", 1);
        var asset = Assert.Single(await database.Store.GetMediaAssetsAsync(profile.Id));
        var result = await ValidateAllAsync(database.Store, profile, BaseTime.AddMinutes(3));
        var validated = (await database.Store.GetByIdAsync(profile.Id))!;

        var changed = Assert.Single(await database.Store.MarkMediaAssetsIntegrityFailedAsync(
            profile.Id,
            validated.UpdatedAtUtc,
            [asset.Id],
            BaseTime.AddMinutes(6)));

        Assert.Equal(MediaAssetState.IntegrityFailed, changed.State);
        Assert.Equal(
            "Registered media failed integrity verification; exclude or repair it before validation.",
            changed.ValidationFailure);
        Assert.Equal(ProfileReadiness.MediaIntegrityFailed.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
        Assert.Equal(result, await database.Store.GetMediaValidationResultAsync(asset.Id));

        var restarted = new SqliteProfileStore(database.DatabasePath, createIfMissing: false);
        var persisted = Assert.Single(await restarted.GetMediaAssetsAsync(profile.Id));
        Assert.Equal(MediaAssetState.IntegrityFailed, persisted.State);
        Assert.Equal(changed.ValidationFailure, persisted.ValidationFailure);
        Assert.Equal(result, await restarted.GetMediaValidationResultAsync(asset.Id));
        Assert.Equal(ProfileReadiness.MediaIntegrityFailed.ToString(),
            (await restarted.GetByIdAsync(profile.Id))!.Readiness);

        var rejectedJobId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.StartMediaValidationJobAsync(
                profile.Id,
                BaseTime.AddMinutes(6),
                rejectedJobId,
                "Processing/validation/integrity-rejected",
                BaseTime.AddMinutes(7)));
        Assert.Null(await restarted.GetProcessingJobAsync(rejectedJobId));
    }

    [Fact]
    public async Task ActiveIntegrityFailureOutranksDraftAndArchivedFailuresDoNotBlockEligibleMedia()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Integrity archive", 2);
        await ValidateAllAsync(database.Store, profile, BaseTime.AddMinutes(3));
        var validated = (await database.Store.GetByIdAsync(profile.Id))!;
        var assets = await database.Store.GetMediaAssetsAsync(profile.Id);
        var failedAsset = assets[0];
        var unlinked = new StoredTrainingVideo(
            Guid.NewGuid(),
            @"D:\media\not-ingested.mp4",
            "new",
            TrainingCondition.VerifiedSincereTruth,
            IsArchived: false,
            SortOrder: 2);
        await database.Store.UpdateAsync(
            validated with
            {
                UpdatedAtUtc = BaseTime.AddMinutes(6),
                TrainingVideos = [.. validated.TrainingVideos, unlinked],
            },
            validated.UpdatedAtUtc);
        var draft = (await database.Store.GetByIdAsync(profile.Id))!;
        Assert.Equal(ProfileReadiness.Draft.ToString(), draft.Readiness);

        await database.Store.MarkMediaAssetsIntegrityFailedAsync(
            profile.Id,
            draft.UpdatedAtUtc,
            [failedAsset.Id],
            BaseTime.AddMinutes(7));

        var repairRequired = (await database.Store.GetByIdAsync(profile.Id))!;
        Assert.Equal(ProfileReadiness.MediaIntegrityFailed.ToString(), repairRequired.Readiness);
        await database.Store.UpdateAsync(
            repairRequired with
            {
                UpdatedAtUtc = BaseTime.AddMinutes(8),
                TrainingVideos = repairRequired.TrainingVideos
                    .Select(video =>
                        video.MediaAssetId == failedAsset.Id || video.Id == unlinked.Id
                            ? video with { IsArchived = true }
                            : video)
                    .ToArray(),
            },
            repairRequired.UpdatedAtUtc);

        Assert.Equal(ProfileReadiness.MediaValidated.ToString(),
            (await database.Store.GetByIdAsync(profile.Id))!.Readiness);
        Assert.Equal(MediaAssetState.IntegrityFailed,
            (await database.Store.GetMediaAssetsAsync(profile.Id))
                .Single(asset => asset.Id == failedAsset.Id)
                .State);
    }

    [Fact]
    public async Task IntegrityFailureBatchValidationAndConcurrencyAreAtomic()
    {
        using var database = new TestDatabase();
        var profile = await AddIngestedProfileAsync(database.Store, "Integrity atomic", 2);
        var otherProfile = await AddIngestedProfileAsync(database.Store, "Integrity foreign", 1);
        var assets = await database.Store.GetMediaAssetsAsync(profile.Id);
        var foreignAsset = Assert.Single(await database.Store.GetMediaAssetsAsync(otherProfile.Id));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.MarkMediaAssetsIntegrityFailedAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                [],
                BaseTime.AddMinutes(3)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.MarkMediaAssetsIntegrityFailedAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                [assets[0].Id, assets[0].Id],
                BaseTime.AddMinutes(3)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Store.MarkMediaAssetsIntegrityFailedAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                [assets[0].Id, Guid.NewGuid()],
                BaseTime.AddMinutes(3)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.Store.MarkMediaAssetsIntegrityFailedAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                [assets[0].Id, foreignAsset.Id],
                BaseTime.AddMinutes(3)));
        await Assert.ThrowsAsync<ProfileConcurrencyConflictException>(() =>
            database.Store.MarkMediaAssetsIntegrityFailedAsync(
                profile.Id,
                profile.UpdatedAtUtc.AddTicks(-1),
                [assets[0].Id],
                BaseTime.AddMinutes(3)));

        Assert.All(await database.Store.GetMediaAssetsAsync(profile.Id),
            asset => Assert.Equal(MediaAssetState.AwaitingProbe, asset.State));
        var unchanged = (await database.Store.GetByIdAsync(profile.Id))!;
        Assert.Equal(profile.UpdatedAtUtc, unchanged.UpdatedAtUtc);
        Assert.Equal(ProfileReadiness.MediaIngestedAwaitingProbe.ToString(), unchanged.Readiness);

        var validationJobId = Guid.NewGuid();
        await database.Store.StartMediaValidationJobAsync(
            profile.Id,
            unchanged.UpdatedAtUtc,
            validationJobId,
            "Processing/validation/active",
            BaseTime.AddMinutes(3));
        await Assert.ThrowsAsync<ProfileProcessingActiveException>(() =>
            database.Store.MarkMediaAssetsIntegrityFailedAsync(
                profile.Id,
                BaseTime.AddMinutes(3),
                [assets[0].Id],
                BaseTime.AddMinutes(4)));
        Assert.All(await database.Store.GetMediaAssetsAsync(profile.Id),
            asset => Assert.Equal(MediaAssetState.AwaitingProbe, asset.State));
    }

    private static async Task<StoredMediaValidationResult> ValidateAllAsync(
        SqliteProfileStore store,
        StoredProfile profile,
        DateTimeOffset startedAtUtc)
    {
        var jobId = Guid.NewGuid();
        await store.StartMediaValidationJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "Processing/validation/success",
            startedAtUtc);
        var assets = await store.GetMediaAssetsForValidationJobAsync(jobId);
        var validatedAtUtc = startedAtUtc.AddMinutes(1);
        var results = assets
            .Select((asset, index) => Result(asset.Id, (char)('a' + index), validatedAtUtc))
            .ToArray();
        await store.CompleteMediaValidationJobAsync(
            jobId,
            results.Select(result => new MediaValidationRegistration(
                result.MediaAssetId,
                MediaAssetState.Validated,
                result,
                null)).ToArray(),
            startedAtUtc.AddMinutes(2));
        return results[0];
    }

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
        var jobId = Guid.NewGuid();
        await store.StartLocalMediaIngestJobAsync(
            profile.Id,
            profile.UpdatedAtUtc,
            jobId,
            "Processing/ingest/job",
            videoCount,
            videoCount * 10L,
            BaseTime.AddMinutes(1));
        await store.CompleteLocalMediaIngestJobAsync(
            jobId,
            videos.Select((video, index) => new MediaAssetRegistration(
                video.Id,
                Guid.NewGuid(),
                new string((char)('1' + index), 64),
                $"Media/{index}.mp4",
                10)).ToArray(),
            BaseTime.AddMinutes(2));
        return (await store.GetByIdAsync(profile.Id))!;
    }
}
