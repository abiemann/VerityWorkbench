using System.Security.Cryptography;
using System.Text;

namespace VerityWorkbench.Media.Tests;

public sealed class MediaPreprocessingServiceTests
{
    [Fact]
    public async Task PrepareUsesFrozenCpuContractAndWritesPrivateCanonicalArtifacts()
    {
        using var test = new PreprocessingTestContext();
        test.QueueSuccessfulPreparation();

        var staged = await test.PrepareAsync();

        Assert.Equal(5, test.Runner.Invocations.Count);
        var generation = test.Runner.Invocations[2];
        Assert.Equal(test.FfmpegPath, generation.ExecutablePath);
        Assert.Contains("-hwaccel", generation.Arguments);
        Assert.Contains("none", generation.Arguments);
        Assert.Contains("mpeg4", generation.Arguments);
        Assert.Contains("aac", generation.Arguments);
        Assert.Contains("pcm_s16le", generation.Arguments);
        Assert.Contains("yuv420p", generation.Arguments);
        Assert.DoesNotContain("libopenh264", generation.Arguments);
        Assert.DoesNotContain("h264_mf", generation.Arguments);
        Assert.DoesNotContain("http", generation.Arguments);
        Assert.All(test.Runner.Invocations, invocation =>
            Assert.Equal(test.JobDirectory, invocation.WorkingDirectoryPath));

        var manifest = await File.ReadAllTextAsync(
            Path.Combine(staged.StagedOutputDirectoryPath, "preprocessing-manifest.json"));
        var map = await File.ReadAllTextAsync(
            Path.Combine(staged.StagedOutputDirectoryPath, "timestamp-map.json"));
        Assert.DoesNotContain(test.Workspace.Root, manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(test.SourcePath, manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(test.Workspace.Root, map, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not exact source-frame lineage", map, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(250_000, staged.Output.SourceTimelineOriginMicroseconds);
        Assert.Equal(MediaPreprocessingService.NotAssessed, staged.Output.MediaQualityState);
        Assert.Equal(MediaPreprocessingService.NotAssessed, staged.Output.ModelApplicabilityState);
    }

    [Fact]
    public async Task CancellationLeavesOnlyUnlockedProcessingDataAndDoesNotPromote()
    {
        using var test = new PreprocessingTestContext();
        test.Runner.Enqueue(Exited(VideoFirstPtsJson));
        test.Runner.Enqueue(Exited(AudioFirstPtsJson));
        test.Runner.Enqueue((invocation, _) =>
        {
            var proxyPart = ArgumentFollowing(invocation.Arguments, "mp4");
            File.WriteAllBytes(proxyPart, [1, 2, 3]);
            return Task.FromCanceled<BoundedProcessResult>(new CancellationToken(canceled: true));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.PrepareAsync());

        Assert.False(Directory.Exists(Path.Combine(test.AssetDirectory, "Prepared")));
        using (File.Open(test.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        var staged = Directory.EnumerateDirectories(
            Path.Combine(test.JobDirectory, "Output"),
            "*",
            SearchOption.TopDirectoryOnly).Single();
        Directory.Delete(staged, recursive: true);
    }

    [Fact]
    public async Task SourceMutationIsPreventedWhileExternalGenerationRuns()
    {
        using var test = new PreprocessingTestContext();
        test.Runner.Enqueue(Exited(VideoFirstPtsJson));
        test.Runner.Enqueue(Exited(AudioFirstPtsJson));
        test.Runner.Enqueue((invocation, _) =>
        {
            Assert.Throws<IOException>(() =>
                File.Open(test.SourcePath, FileMode.Open, FileAccess.Write, FileShare.None));
            test.WriteGeneratedParts(invocation);
            return Task.FromResult(Exited(GenerationProgress, GenerationStatistics));
        });
        test.Runner.Enqueue(Exited(ProxyProbeJson));
        test.Runner.Enqueue(Exited(AudioProbeJson));

        var staged = await test.PrepareAsync();

        Assert.True(File.Exists(Path.Combine(staged.StagedOutputDirectoryPath, "proxy.mp4")));
    }

    [Fact]
    public async Task PrepareRejectsValidationLineageThatDoesNotMatchPreflight()
    {
        using var test = new PreprocessingTestContext();
        var mismatchedValidation = test.Validation with
        {
            ValidationContractSha256 = HashText("different-validation-contract"),
        };

        var exception = await Assert.ThrowsAsync<MediaPreprocessingException>(() =>
            test.Service.PrepareAsync(
                test.Workspace.Layout,
                test.JobDirectory,
                new(
                    test.JobId,
                    test.AssetId,
                    test.SourcePath,
                    test.SourceSha256,
                    test.SourceLength,
                    mismatchedValidation),
                test.Tools,
                test.Preflight));

        Assert.Equal(MediaPreprocessingFailure.PreflightMismatch, exception.Failure);
        Assert.Empty(test.Runner.Invocations);
    }

    [Theory]
    [InlineData(true, true, 1, 0, 0)]
    [InlineData(true, false, 1, 0, 0)]
    [InlineData(false, true, 0, 1, 0)]
    [InlineData(false, false, 0, 0, 1)]
    public async Task ReconciliationHandlesCommittedAndUncommittedMoveStates(
        bool committed,
        bool leftPromoted,
        int completed,
        int rolledBack,
        int cleared)
    {
        using var workspace = new TestWorkspace();
        var service = new MediaPreprocessingService();
        var staged = CreateSyntheticStagedResult(workspace);
        var promoted = await service.PromoteAsync(workspace.Layout, staged);
        if (!leftPromoted)
        {
            Directory.Move(promoted.PreparedDirectoryPath, promoted.OriginatingStagedDirectoryPath);
        }

        IReadOnlyDictionary<Guid, string> committedPaths = committed
            ? new Dictionary<Guid, string>
            {
                [promoted.Output.MediaAssetId] = promoted.Output.ManifestWorkspaceRelativePath,
            }
            : new Dictionary<Guid, string>();
        var result = await service.ReconcilePendingPromotionsAsync(
            workspace.Layout,
            committedPaths,
            new HashSet<Guid> { promoted.JobId });

        Assert.Equal(completed, result.CompletedCount);
        Assert.Equal(rolledBack, result.RolledBackCount);
        Assert.Equal(cleared, result.ClearedCount);
        Assert.Equal(0, result.WarningCount);
        Assert.Empty(result.IntegrityFailedAssetIds);
        Assert.Equal(committed, Directory.Exists(promoted.PreparedDirectoryPath));
        Assert.Equal(!committed, Directory.Exists(promoted.OriginatingStagedDirectoryPath));
    }

    [Fact]
    public async Task ReconciliationReportsCommittedHashMismatchWithoutMovingOrDeleting()
    {
        using var workspace = new TestWorkspace();
        var service = new MediaPreprocessingService();
        var staged = CreateSyntheticStagedResult(workspace);
        var promoted = await service.PromoteAsync(workspace.Layout, staged);
        await File.AppendAllTextAsync(
            Path.Combine(promoted.PreparedDirectoryPath, "audio.wav"),
            "changed");

        var result = await service.ReconcilePendingPromotionsAsync(
            workspace.Layout,
            new Dictionary<Guid, string>
            {
                [promoted.Output.MediaAssetId] = promoted.Output.ManifestWorkspaceRelativePath,
            },
            new HashSet<Guid> { promoted.JobId });

        Assert.Contains(promoted.Output.MediaAssetId, result.IntegrityFailedAssetIds);
        Assert.True(result.WarningCount > 0);
        Assert.True(Directory.Exists(promoted.PreparedDirectoryPath));
        Assert.False(Directory.Exists(promoted.OriginatingStagedDirectoryPath));
    }

    [Fact]
    public async Task VerifyPreparedReturnsSafeFailureForTamperedCommittedBundle()
    {
        using var workspace = new TestWorkspace();
        var service = new MediaPreprocessingService();
        var staged = CreateSyntheticStagedResult(workspace);
        var promoted = await service.PromoteAsync(workspace.Layout, staged);
        service.ConfirmPromotion(workspace.Layout, promoted);
        await File.AppendAllTextAsync(
            Path.Combine(promoted.PreparedDirectoryPath, "proxy.mp4"),
            "changed");

        var result = await service.VerifyPreparedAsync(workspace.Layout, promoted.Output);

        Assert.False(result.IsValid);
        Assert.Equal(MediaPreparedVerificationState.IntegrityMismatch, result.State);
        Assert.DoesNotContain(workspace.Root, result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyPreparedDoesNotCallATransientReadFailureIntegrityMismatch()
    {
        using var workspace = new TestWorkspace();
        var service = new MediaPreprocessingService();
        var staged = CreateSyntheticStagedResult(workspace);
        var promoted = await service.PromoteAsync(workspace.Layout, staged);
        service.ConfirmPromotion(workspace.Layout, promoted);
        await using var exclusiveLock = new FileStream(
            Path.Combine(promoted.PreparedDirectoryPath, "proxy.mp4"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = await service.VerifyPreparedAsync(workspace.Layout, promoted.Output);

        Assert.False(result.IsValid);
        Assert.Equal(MediaPreparedVerificationState.OperationalFailure, result.State);
        Assert.DoesNotContain(workspace.Root, result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    private static StagedMediaPreprocessingResult CreateSyntheticStagedResult(TestWorkspace workspace)
    {
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var jobDirectory = Path.Combine(workspace.Layout.ProcessingRoot, "synthetic-job");
        var stagedDirectory = Path.Combine(jobDirectory, "Output", assetId.ToString("N"));
        var assetDirectory = Path.Combine(
            workspace.Layout.MediaRoot,
            "asset_" + assetId.ToString("N")[..12]);
        var contractHash = HashText("contract");
        var preparedDirectory = Path.Combine(
            assetDirectory,
            "Prepared",
            "v1_" + contractHash[..12]);
        Directory.CreateDirectory(stagedDirectory);
        Directory.CreateDirectory(assetDirectory);
        File.WriteAllBytes(Path.Combine(assetDirectory, "original.mp4"), [1]);

        var proxy = WriteArtifact(stagedDirectory, "proxy.mp4", "proxy");
        var audio = WriteArtifact(stagedDirectory, "audio.wav", "audio");
        var map = WriteArtifact(stagedDirectory, "timestamp-map.json", "map");
        var manifest = WriteArtifact(stagedDirectory, "preprocessing-manifest.json", "manifest");
        var preparedRelative = Path.GetRelativePath(workspace.Layout.WorkspaceRoot, preparedDirectory)
            .Replace('\\', '/');
        var output = new MediaPreprocessingResult(
            assetId,
            HashText("source"),
            1,
            MediaPreprocessingService.CurrentPreprocessingContractVersion,
            contractHash,
            preparedRelative + "/proxy.mp4",
            proxy.Sha256,
            proxy.ByteLength,
            "mp4",
            "mpeg4",
            "yuv420p",
            320,
            240,
            30,
            1,
            "aac",
            48_000,
            2,
            1_000_000,
            preparedRelative + "/audio.wav",
            audio.Sha256,
            audio.ByteLength,
            "pcm_s16le",
            16_000,
            1,
            16_000,
            1_000_000,
            preparedRelative + "/timestamp-map.json",
            map.Sha256,
            map.ByteLength,
            preparedRelative + "/preprocessing-manifest.json",
            manifest.Sha256,
            manifest.ByteLength,
            0,
            1_000_000,
            1,
            1,
            "n8.1-test",
            "compiler",
            HashText("configuration"),
            HashText("ffmpeg"),
            HashText("validation"),
            MediaPreprocessingService.NotAssessed,
            MediaPreprocessingService.NotAssessed,
            DateTimeOffset.UtcNow);
        return new(jobId, stagedDirectory, preparedDirectory, output);
    }

    private static (string Sha256, long ByteLength) WriteArtifact(
        string directory,
        string fileName,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(Path.Combine(directory, fileName), bytes);
        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength);
    }

    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ArgumentFollowing(IReadOnlyList<string> arguments, string format)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index] == "-f" && arguments[index + 1] == format)
            {
                return arguments[index + 2];
            }
        }

        throw new InvalidOperationException("The expected output format was not found.");
    }

    private static BoundedProcessResult Exited(string stdout, string stderr = "") =>
        new(ProcessTermination.Exited, 0, stdout, stderr);

    private const string VideoFirstPtsJson =
        """
        { "frames": [ { "stream_index": 0, "best_effort_timestamp_time": "0.250000" } ] }
        """;

    private const string AudioFirstPtsJson =
        """
        { "frames": [ { "stream_index": 1, "best_effort_timestamp_time": "0.500000" } ] }
        """;

    private const string GenerationProgress = "out_time_us=1250000\nprogress=end\n";

    private const string GenerationStatistics =
        """
        [Parsed_vfrdet_0 @ 1] VFR:0.250000 (3/9)
        [Parsed_astats_1 @ 2] Overall
        [Parsed_astats_1 @ 2] Peak level dB: -1.500000
        [Parsed_astats_1 @ 2] RMS level dB: -20.000000
        [Parsed_astats_1 @ 2] Peak count: 2.000000
        [Parsed_astats_1 @ 2] Number of samples: 16000
        """;

    private const string ProxyProbeJson =
        """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "mpeg4", "pix_fmt": "yuv420p",
              "width": 640, "height": 360, "avg_frame_rate": "30/1" },
            { "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": 2 }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "1.250000" }
        }
        """;

    private const string AudioProbeJson =
        """
        {
          "streams": [
            { "codec_type": "audio", "codec_name": "pcm_s16le", "sample_rate": "16000",
              "channels": 1, "time_base": "1/16000", "duration_ts": 20000 }
          ],
          "format": { "format_name": "wav", "duration": "1.250000" }
        }
        """;

    private sealed class PreprocessingTestContext : IDisposable
    {
        public PreprocessingTestContext()
        {
            Workspace = new TestWorkspace();
            JobId = Guid.NewGuid();
            AssetId = Guid.NewGuid();
            JobDirectory = Path.Combine(Workspace.Layout.ProcessingRoot, "preprocessing-job");
            AssetDirectory = Path.Combine(
                Workspace.Layout.MediaRoot,
                "asset_" + AssetId.ToString("N")[..12]);
            Directory.CreateDirectory(JobDirectory);
            Directory.CreateDirectory(AssetDirectory);
            SourcePath = Path.Combine(AssetDirectory, "original.mp4");
            File.WriteAllBytes(SourcePath, Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
            SourceLength = new FileInfo(SourcePath).Length;
            SourceSha256 = HashFile(SourcePath);

            var toolsDirectory = Path.Combine(Workspace.Root, "tools");
            Directory.CreateDirectory(toolsDirectory);
            FfprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
            FfmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
            File.WriteAllText(FfprobePath, "ffprobe");
            File.WriteAllText(FfmpegPath, "ffmpeg");
            var ffprobeHash = HashFile(FfprobePath);
            var ffmpegHash = HashFile(FfmpegPath);
            var configurationHash = HashText("--test");
            var ffprobeProvenance = new MediaValidationToolProvenance(
                "n8.1-test", "compiler", "--test", configurationHash, ffprobeHash);
            var ffmpegProvenance = new MediaValidationToolProvenance(
                "n8.1-test", "compiler", "--test", configurationHash, ffmpegHash);
            var validationContract = HashText("validation-contract");
            Preflight = new(ffprobeProvenance, ffmpegProvenance, validationContract);
            Tools = new(
                new(FfprobePath, ffprobeHash, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 1024 * 1024, 1024 * 1024),
                new(FfmpegPath, ffmpegHash, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 1024 * 1024, 1024 * 1024),
                "n8.1",
                MediaValidationService.CurrentValidationContractVersion);
            Validation = new(
                "mp4",
                "isom",
                1_250_000,
                new(0, "mpeg4", 640, 360, 30, 1),
                new(1, "aac", 48_000, 2),
                ffprobeProvenance,
                ffmpegProvenance,
                validationContract,
                1_250_000);
            Service = new(Runner);
        }

        public TestWorkspace Workspace { get; }
        public Guid JobId { get; }
        public Guid AssetId { get; }
        public string JobDirectory { get; }
        public string AssetDirectory { get; }
        public string SourcePath { get; }
        public long SourceLength { get; }
        public string SourceSha256 { get; }
        public string FfprobePath { get; }
        public string FfmpegPath { get; }
        public MediaValidationToolContract Tools { get; }
        public MediaValidationPreflight Preflight { get; }
        public ValidatedMediaMetadata Validation { get; }
        public FakeProcessRunner Runner { get; } = new();
        public MediaPreprocessingService Service { get; }

        public void QueueSuccessfulPreparation()
        {
            Runner.Enqueue(Exited(VideoFirstPtsJson));
            Runner.Enqueue(Exited(AudioFirstPtsJson));
            Runner.Enqueue((invocation, _) =>
            {
                WriteGeneratedParts(invocation);
                return Task.FromResult(Exited(GenerationProgress, GenerationStatistics));
            });
            Runner.Enqueue(Exited(ProxyProbeJson));
            Runner.Enqueue(Exited(AudioProbeJson));
        }

        public void WriteGeneratedParts(ProcessInvocation invocation)
        {
            File.WriteAllBytes(ArgumentFollowing(invocation.Arguments, "mp4"), Encoding.UTF8.GetBytes("proxy"));
            File.WriteAllBytes(ArgumentFollowing(invocation.Arguments, "wav"), Encoding.UTF8.GetBytes("audio"));
        }

        public Task<StagedMediaPreprocessingResult> PrepareAsync() =>
            Service.PrepareAsync(
                Workspace.Layout,
                JobDirectory,
                new(JobId, AssetId, SourcePath, SourceSha256, SourceLength, Validation),
                Tools,
                Preflight);

        public void Dispose() => Workspace.Dispose();

        private static string HashFile(string path) =>
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private sealed class FakeProcessRunner : IBoundedProcessRunner
    {
        private readonly Queue<Func<ProcessInvocation, CancellationToken, Task<BoundedProcessResult>>> _results = [];
        public List<ProcessInvocation> Invocations { get; } = [];

        public void Enqueue(BoundedProcessResult result) =>
            Enqueue((_, _) => Task.FromResult(result));

        public void Enqueue(Func<ProcessInvocation, CancellationToken, Task<BoundedProcessResult>> result) =>
            _results.Enqueue(result);

        public Task<BoundedProcessResult> RunAsync(
            string executablePath,
            string workingDirectoryPath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            int maximumStandardOutputBytes,
            int maximumStandardErrorBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocation = new ProcessInvocation(executablePath, workingDirectoryPath, [.. arguments]);
            Invocations.Add(invocation);
            return _results.Dequeue()(invocation, cancellationToken);
        }
    }

    private sealed record ProcessInvocation(
        string ExecutablePath,
        string WorkingDirectoryPath,
        IReadOnlyList<string> Arguments);
}
