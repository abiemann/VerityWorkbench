using System.Security.Cryptography;

namespace VerityWorkbench.Media.Tests;

public sealed class LocalMediaStagingServiceTests
{
    private static readonly DateTimeOffset JobTime =
        new(2026, 8, 15, 12, 34, 56, TimeSpan.FromHours(-7));

    [Fact]
    public async Task StageCopiesExactBytesAndReturnsIntegrityMetadataWithoutChangingSource()
    {
        using var workspace = new TestWorkspace();
        var bytes = Enumerable.Range(0, 300_000).Select(index => (byte)(index % 251)).ToArray();
        var sourcePath = workspace.CreateSource("Interview.MP4", bytes);
        var sourceWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        var trainingVideoId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var reports = new CapturingProgress();

        var result = await new LocalMediaStagingService().StageAsync(
            workspace.Layout,
            jobId,
            JobTime,
            [new(trainingVideoId, sourcePath)],
            reports);

        var item = Assert.Single(result.Items);
        Assert.Equal(jobId, result.JobId);
        Assert.Equal(JobTime.ToUniversalTime(), result.CreatedAtUtc);
        Assert.Equal(trainingVideoId, item.TrainingVideoId);
        Assert.Equal("Interview.MP4", item.SourceFileName);
        Assert.Equal(bytes.LongLength, item.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), item.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(item.StagedFilePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.Equal(sourceWriteTime, File.GetLastWriteTimeUtc(sourcePath));
        Assert.StartsWith(
            Path.GetFullPath(workspace.Layout.ProcessingRoot) + Path.DirectorySeparatorChar,
            Path.GetFullPath(result.JobDirectoryPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(result.JobDirectoryPath, "items", trainingVideoId.ToString("N")), item.StagedDirectoryPath);
        Assert.Equal(Path.Combine(item.StagedDirectoryPath, "original.mp4"), item.StagedFilePath);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(result.JobDirectoryPath, "*", SearchOption.AllDirectories),
            path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(bytes.LongLength, reports.Values[^1].BytesCopied);
        Assert.Equal(bytes.LongLength, reports.Values[^1].TotalBytes);
    }

    [Fact]
    public async Task CancellationLeavesOnlyProcessingDataAndReleasesSourceHandle()
    {
        using var workspace = new TestWorkspace();
        var sourcePath = workspace.CreateSource("large.mp4", new byte[CopySizedPayload]);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelAfterBytesProgress(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalMediaStagingService().StageAsync(
                workspace.Layout,
                Guid.NewGuid(),
                JobTime,
                [new(Guid.NewGuid(), sourcePath)],
                progress,
                cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Layout.MediaRoot));
        Assert.Contains(
            Directory.EnumerateFiles(workspace.Layout.ProcessingRoot, "*.part", SearchOption.AllDirectories),
            _ => true);

        using (new FileStream(sourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        File.Delete(sourcePath);
        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public async Task RejectsRelativeNonMp4AndMissingSourcePathsBeforeCreatingJob()
    {
        using var workspace = new TestWorkspace();
        var service = new LocalMediaStagingService();
        var movPath = workspace.CreateSource("clip.mov", [1, 2, 3]);
        var missingMp4 = Path.Combine(workspace.Sources, "missing.mp4");

        await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(
            workspace.Layout,
            Guid.NewGuid(),
            JobTime,
            [new(Guid.NewGuid(), Path.Combine("relative", "clip.mp4"))]));

        await Assert.ThrowsAsync<ArgumentException>(() => service.StageAsync(
            workspace.Layout,
            Guid.NewGuid(),
            JobTime,
            [new(Guid.NewGuid(), movPath)]));

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.StageAsync(
            workspace.Layout,
            Guid.NewGuid(),
            JobTime,
            [new(Guid.NewGuid(), missingMp4)]));

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Layout.ProcessingRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Layout.MediaRoot));
    }

    [Fact]
    public async Task RejectsDuplicateTrainingVideoIdsBeforeCreatingJob()
    {
        using var workspace = new TestWorkspace();
        var first = workspace.CreateSource("first.mp4", [1]);
        var second = workspace.CreateSource("second.mp4", [2]);
        var duplicateId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LocalMediaStagingService().StageAsync(
                workspace.Layout,
                Guid.NewGuid(),
                JobTime,
                [new(duplicateId, first), new(duplicateId, second)]));

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Layout.ProcessingRoot));
    }

    [Fact]
    public async Task RejectsZeroByteSourceBeforeCreatingJob()
    {
        using var workspace = new TestWorkspace();
        var emptySource = workspace.CreateSource("empty.mp4", []);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LocalMediaStagingService().StageAsync(
                workspace.Layout,
                Guid.NewGuid(),
                JobTime,
                [new(Guid.NewGuid(), emptySource)]));

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Layout.ProcessingRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Layout.MediaRoot));
    }

    [Fact]
    public async Task JobPathIsDeterministicRelativeAndBounded()
    {
        using var workspace = new TestWorkspace();
        var jobId = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");
        var expected = Path.Combine(
            "Processing",
            "20260815T1934560000000Z_local-media_1234567890ab");

        Assert.Equal(expected, LocalMediaStagingService.BuildJobRelativePath(jobId, JobTime));
        Assert.False(Path.IsPathFullyQualified(expected));
        Assert.DoesNotContain("..", expected, StringComparison.Ordinal);

        var source = workspace.CreateSource("one.mp4", [5, 4, 3]);
        var result = await new LocalMediaStagingService().StageAsync(
            workspace.Layout,
            jobId,
            JobTime,
            [new(Guid.NewGuid(), source)]);

        Assert.Equal(Path.Combine(workspace.Layout.WorkspaceRoot, expected), result.JobDirectoryPath);
    }

    [Fact]
    public async Task PromotionRejectsForgedStagedPathOutsideProcessing()
    {
        using var workspace = new TestWorkspace();
        var outsideDirectory = Path.Combine(workspace.Root, "outside", "items", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "original.mp4");
        await File.WriteAllBytesAsync(outsideFile, [1, 2, 3]);
        var forged = new StagedLocalMediaItem(
            Guid.NewGuid(),
            Guid.Parse(Path.GetFileName(outsideDirectory)),
            Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])),
            3,
            "source.mp4",
            outsideDirectory,
            outsideFile);

        await Assert.ThrowsAsync<IOException>(() =>
            new LocalMediaStagingService().PromoteAsync(
                workspace.Layout,
                forged,
                "recording",
                "source.mp4",
                Guid.NewGuid()));

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Layout.MediaRoot));
        Assert.True(File.Exists(outsideFile));
    }

    [Fact]
    public async Task PromotionAtomicallyMovesCompletedItemAndRollbackRestoresItWithoutDeletingBytes()
    {
        using var workspace = new TestWorkspace();
        var bytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 199)).ToArray();
        var source = workspace.CreateSource("Camera Angle 1.mp4", bytes);
        var trainingVideoId = Guid.NewGuid();
        var service = new LocalMediaStagingService();
        var stagedJob = await service.StageAsync(
            workspace.Layout,
            Guid.NewGuid(),
            JobTime,
            [new(trainingVideoId, source)]);
        var staged = Assert.Single(stagedJob.Items);
        var assetId = Guid.Parse("abcdef12-3456-7890-abcd-ef1234567890");

        var promoted = await service.PromoteAsync(
            workspace.Layout,
            staged,
            "2026/01/01",
            "Camera Angle 1.mp4",
            assetId);

        Assert.False(Directory.Exists(staged.StagedDirectoryPath));
        Assert.True(Directory.Exists(promoted.AssetDirectoryPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(promoted.OriginalFilePath));
        Assert.Equal(
            "Media/2026-01-01_camera-angle-1_abcdef123456/original.mp4",
            promoted.WorkspaceRelativeOriginalPath);
        Assert.EndsWith("_abcdef123456", promoted.AssetDirectoryPath, StringComparison.OrdinalIgnoreCase);
        var journalPath = Assert.Single(Directory.EnumerateFiles(
            stagedJob.JobDirectoryPath,
            "*.json",
            SearchOption.AllDirectories));
        Assert.DoesNotContain(source, await File.ReadAllTextAsync(journalPath), StringComparison.OrdinalIgnoreCase);

        service.RollbackPromotion(workspace.Layout, promoted);

        Assert.False(Directory.Exists(promoted.AssetDirectoryPath));
        Assert.True(Directory.Exists(staged.StagedDirectoryPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(staged.StagedFilePath));
        Assert.Empty(Directory.EnumerateFiles(
            stagedJob.JobDirectoryPath,
            "*.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReconciliationRollsBackUncommittedPromotionWithoutDeletingBytes()
    {
        using var workspace = new TestWorkspace();
        var bytes = Enumerable.Range(0, 8192).Select(index => (byte)(index % 173)).ToArray();
        var source = workspace.CreateSource("recovery.mp4", bytes);
        var service = new LocalMediaStagingService();
        var jobId = Guid.NewGuid();
        var stagedJob = await service.StageAsync(
            workspace.Layout,
            jobId,
            JobTime,
            [new(Guid.NewGuid(), source)]);
        var promoted = await service.PromoteAsync(
            workspace.Layout,
            Assert.Single(stagedJob.Items),
            "recovery",
            "recovery.mp4",
            Guid.NewGuid());

        var result = await service.ReconcilePendingPromotionsAsync(
            workspace.Layout,
            new Dictionary<Guid, string>(),
            new HashSet<Guid> { jobId });

        Assert.Equal(1, result.RolledBackUncommittedPromotions);
        Assert.Equal(0, result.WarningCount);
        Assert.False(Directory.Exists(promoted.AssetDirectoryPath));
        Assert.True(Directory.Exists(promoted.OriginatingStagedDirectoryPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(
            Path.Combine(promoted.OriginatingStagedDirectoryPath, "original.mp4")));
        Assert.Empty(Directory.EnumerateFiles(
            stagedJob.JobDirectoryPath,
            "*.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReconciliationKeepsCommittedPromotionAndClearsJournal()
    {
        using var workspace = new TestWorkspace();
        var bytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 137)).ToArray();
        var source = workspace.CreateSource("committed.mp4", bytes);
        var service = new LocalMediaStagingService();
        var jobId = Guid.NewGuid();
        var stagedJob = await service.StageAsync(
            workspace.Layout,
            jobId,
            JobTime,
            [new(Guid.NewGuid(), source)]);
        var promoted = await service.PromoteAsync(
            workspace.Layout,
            Assert.Single(stagedJob.Items),
            "committed",
            "committed.mp4",
            Guid.NewGuid());

        var result = await service.ReconcilePendingPromotionsAsync(
            workspace.Layout,
            new Dictionary<Guid, string>
            {
                [promoted.AssetId] = promoted.WorkspaceRelativeOriginalPath,
            },
            new HashSet<Guid> { jobId });

        Assert.Equal(1, result.CompletedCommittedPromotions);
        Assert.Equal(0, result.WarningCount);
        Assert.True(File.Exists(promoted.OriginalFilePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(promoted.OriginalFilePath));
        Assert.Empty(Directory.EnumerateFiles(
            stagedJob.JobDirectoryPath,
            "*.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingAssetVerificationRejectsExternalMutation()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateSource("verify.mp4", [1, 2, 3, 4, 5]);
        var service = new LocalMediaStagingService();
        var stagedJob = await service.StageAsync(
            workspace.Layout,
            Guid.NewGuid(),
            JobTime,
            [new(Guid.NewGuid(), source)]);
        var promoted = await service.PromoteAsync(
            workspace.Layout,
            Assert.Single(stagedJob.Items),
            "verify",
            "verify.mp4",
            Guid.NewGuid());
        service.CommitPromotion(workspace.Layout, promoted);

        await service.VerifyExistingAssetAsync(
            workspace.Layout,
            promoted.AssetId,
            promoted.WorkspaceRelativeOriginalPath,
            promoted.Sha256,
            promoted.ByteLength);

        await File.WriteAllBytesAsync(promoted.OriginalFilePath, [5, 4, 3, 2, 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyExistingAssetAsync(
            workspace.Layout,
            promoted.AssetId,
            promoted.WorkspaceRelativeOriginalPath,
            promoted.Sha256,
            promoted.ByteLength));
    }

    private const int CopySizedPayload = 3 * 128 * 1024;

    private sealed class CapturingProgress : IProgress<LocalMediaStagingProgress>
    {
        public List<LocalMediaStagingProgress> Values { get; } = [];

        public void Report(LocalMediaStagingProgress value) => Values.Add(value);
    }

    private sealed class CancelAfterBytesProgress(CancellationTokenSource cancellation)
        : IProgress<LocalMediaStagingProgress>
    {
        public void Report(LocalMediaStagingProgress value)
        {
            if (value.BytesCopied > 0)
            {
                cancellation.Cancel();
            }
        }
    }
}
