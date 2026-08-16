using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VerityWorkbench.Media.Tests;

public sealed class MediaValidationServiceTests
{
    [Fact]
    public async Task ValidatesPinnedToolsSelectsExplicitDefaultsAndFullyDecodesWithoutDerivatives()
    {
        using var test = new ValidationTestContext();
        test.QueueSuccessfulPreflight();
        test.Runner.Enqueue(Exited(ProbeJson(
            Video(0, isDefault: false),
            Video(2, isDefault: true, codec: "hevc"),
            Audio(1, isDefault: true),
            Audio(3, isDefault: false, codec: "opus"))));
        test.Runner.Enqueue(Exited(DecodeProgress(1_250_000)));

        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.WorkingDirectory,
            test.Tools);
        var result = await test.ValidateAsync(preflight);

        Assert.Equal("mp4", result.ContainerFormat);
        Assert.Equal("isom", result.ContainerMajorBrand);
        Assert.Equal(1_250_000, result.DurationMicroseconds);
        Assert.Equal(2, result.Video.StreamIndex);
        Assert.Equal("hevc", result.Video.CodecName);
        Assert.Equal(1920, result.Video.Width);
        Assert.Equal(1080, result.Video.Height);
        Assert.Equal(30, result.Video.FrameRateNumerator);
        Assert.Equal(1, result.Video.FrameRateDenominator);
        Assert.Equal(1, result.Audio.StreamIndex);
        Assert.Equal(48_000, result.Audio.SampleRateHz);
        Assert.Equal(2, result.Audio.ChannelCount);
        Assert.True(result.DecodeCompleted);
        Assert.Equal(1_250_000, result.DecodedDurationMicroseconds);
        Assert.Equal(ValidationTestContext.Version, result.Ffprobe.Version);
        Assert.Equal(ValidationTestContext.Configuration, result.Ffmpeg.Configuration);
        Assert.Equal(test.FfprobeSha256, result.Ffprobe.ExecutableSha256);
        Assert.Equal(test.FfmpegSha256, result.Ffmpeg.ExecutableSha256);
        Assert.Equal(64, result.ValidationContractSha256.Length);
        Assert.Empty(Directory.EnumerateFiles(test.WorkingDirectory));

        Assert.Equal(4, test.Runner.Invocations.Count);
        Assert.All(test.Runner.Invocations, invocation =>
            Assert.Equal(test.WorkingDirectory, invocation.WorkingDirectoryPath));
        var probe = test.Runner.Invocations[2];
        Assert.Equal(test.FfprobePath, probe.ExecutablePath);
        Assert.Equal(test.MediaPath, probe.Arguments[^1]);
        Assert.Equal(
            [
                "-v", "error", "-protocol_whitelist", "file,pipe", "-show_format",
                "-show_streams", "-of", "json", test.MediaPath,
            ],
            probe.Arguments);

        var decode = test.Runner.Invocations[3];
        Assert.Equal(test.FfmpegPath, decode.ExecutablePath);
        Assert.Contains("none", decode.Arguments);
        Assert.Contains("file,pipe", decode.Arguments);
        Assert.Contains("0:2", decode.Arguments);
        Assert.Contains("0:1", decode.Arguments);
        Assert.Equal("-", decode.Arguments[^1]);
        Assert.DoesNotContain(test.FfprobePath, JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(test.FfmpegPath, JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsExternalMediaAndWorkingDirectoriesBeforeStartingTools()
    {
        using var test = new ValidationTestContext();
        var externalMedia = test.Workspace.CreateSource("outside.mp4", [1, 2, 3]);
        var externalWorkingDirectory = Path.Combine(test.Workspace.Root, "outside-job");
        Directory.CreateDirectory(externalWorkingDirectory);

        var workingException = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.PreflightAsync(
                test.Workspace.Layout,
                externalWorkingDirectory,
                test.Tools));
        Assert.Equal(MediaValidationFailure.WorkingDirectoryInvalid, workingException.Failure);

        test.QueueSuccessfulPreflight();
        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.WorkingDirectory,
            test.Tools);
        var mediaException = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.ValidateAsync(
                test.Workspace.Layout,
                test.WorkingDirectory,
                externalMedia,
                test.MediaSha256,
                test.MediaByteLength,
                test.Tools,
                preflight));
        Assert.Equal(MediaValidationFailure.MediaPathInvalid, mediaException.Failure);
        Assert.Equal(2, test.Runner.Invocations.Count);
    }

    [Fact]
    public async Task PreflightMayUseProcessingRootButMediaValidationRequiresJobChild()
    {
        using var test = new ValidationTestContext();
        test.QueueSuccessfulPreflight();

        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.Workspace.Layout.ProcessingRoot,
            test.Tools);

        Assert.All(test.Runner.Invocations, invocation =>
            Assert.Equal(test.Workspace.Layout.ProcessingRoot, invocation.WorkingDirectoryPath));
        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.ValidateAsync(
                test.Workspace.Layout,
                test.Workspace.Layout.ProcessingRoot,
                test.MediaPath,
                test.MediaSha256,
                test.MediaByteLength,
                test.Tools,
                preflight));
        Assert.Equal(MediaValidationFailure.WorkingDirectoryInvalid, exception.Failure);
        Assert.Equal(2, test.Runner.Invocations.Count);
    }

    [Fact]
    public async Task RejectsToolHashMismatchBeforeRunningAnyExecutable()
    {
        using var test = new ValidationTestContext();
        var wrongFfprobe = test.Tools with
        {
            Ffprobe = test.Tools.Ffprobe with { ExpectedSha256 = new string('0', 64) },
        };

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.PreflightAsync(
                test.Workspace.Layout,
                test.WorkingDirectory,
                wrongFfprobe));

        Assert.Equal(MediaValidationFailure.ToolIntegrityMismatch, exception.Failure);
        Assert.Empty(test.Runner.Invocations);
    }

    [Fact]
    public async Task RejectsMismatchedFfprobeAndFfmpegBuildsBeforeProbingMedia()
    {
        using var test = new ValidationTestContext();
        test.Runner.Enqueue(Exited(FfprobeIdentity()));
        test.Runner.Enqueue(Exited(FfmpegIdentity(configuration: "--different-build")));

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.PreflightAsync(
                test.Workspace.Layout,
                test.WorkingDirectory,
                test.Tools));

        Assert.Equal(MediaValidationFailure.ToolIdentityMismatch, exception.Failure);
        Assert.Equal(2, test.Runner.Invocations.Count);
    }

    [Fact]
    public async Task VersionPrefixRequiresATokenBoundary()
    {
        using var test = new ValidationTestContext();
        test.Runner.Enqueue(Exited(FfprobeIdentity(version: "n8.10-test")));
        test.Runner.Enqueue(Exited(FfmpegIdentity(version: "n8.10-test")));

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.PreflightAsync(
                test.Workspace.Layout,
                test.WorkingDirectory,
                test.Tools));

        Assert.Equal(MediaValidationFailure.ToolIdentityMismatch, exception.Failure);
    }

    [Fact]
    public async Task RejectsWrongValidationContractVersionBeforeRunningTools()
    {
        using var test = new ValidationTestContext();
        var tools = test.Tools with { ValidationContractVersion = "future.v2" };

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.PreflightAsync(
                test.Workspace.Layout,
                test.WorkingDirectory,
                tools));

        Assert.Equal(MediaValidationFailure.ToolContractInvalid, exception.Failure);
        Assert.Empty(test.Runner.Invocations);
    }

    [Theory]
    [MemberData(nameof(InvalidProbeOutputs))]
    public async Task RejectsInvalidProbeMetadataWithTypedReason(
        string probeOutput,
        MediaValidationFailure expectedFailure)
    {
        using var test = new ValidationTestContext();
        test.QueueSuccessfulPreflight();
        test.Runner.Enqueue(Exited(probeOutput));
        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.WorkingDirectory,
            test.Tools);

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.ValidateAsync(preflight));

        Assert.Equal(expectedFailure, exception.Failure);
        Assert.Equal(3, test.Runner.Invocations.Count);
    }

    public static TheoryData<string, MediaValidationFailure> InvalidProbeOutputs => new()
    {
        { "not-json", MediaValidationFailure.ProbeOutputMalformed },
        { ProbeJsonWith("mov", "1.25", "isom", Video(0, true), Audio(1, true)), MediaValidationFailure.UnsupportedContainer },
        { ProbeJsonWith("mov,mp4,m4a", "0", "isom", Video(0, true), Audio(1, true)), MediaValidationFailure.InvalidDuration },
        { ProbeJsonWith("mov,mp4,m4a", "1.25", "qt  ", Video(0, true), Audio(1, true)), MediaValidationFailure.UnsupportedContainer },
        { ProbeJsonWith("mov,mp4,m4a,3gp", "1.25", "3gp4", Video(0, true), Audio(1, true)), MediaValidationFailure.UnsupportedContainer },
        { ProbeJson(Audio(1, true)), MediaValidationFailure.MissingVideoStream },
        { ProbeJson(Video(0, true)), MediaValidationFailure.MissingAudioStream },
        { ProbeJson(Video(0, true, width: 0), Audio(1, true)), MediaValidationFailure.InvalidVideoStream },
        { ProbeJson(Video(0, true, width: 0), Video(2, false), Audio(1, true)), MediaValidationFailure.InvalidVideoStream },
        { ProbeJson(Video(0, false), Video(2, false), Audio(1, true)), MediaValidationFailure.AmbiguousVideoStreams },
        { ProbeJson(Video(0, true), Audio(1, false), Audio(2, false)), MediaValidationFailure.AmbiguousAudioStreams },
        { ProbeJson(Video(0, true), Audio(1, true, sampleRate: "0")), MediaValidationFailure.InvalidAudioStream },
    };

    [Theory]
    [InlineData((int)ProcessTermination.LaunchFailed, 0, "", MediaValidationFailure.ProbeLaunchFailed)]
    [InlineData((int)ProcessTermination.TimedOut, 0, "", MediaValidationFailure.ProbeTimedOut)]
    [InlineData((int)ProcessTermination.StandardOutputLimitExceeded, 0, "", MediaValidationFailure.ProbeOutputLimitExceeded)]
    [InlineData((int)ProcessTermination.Exited, 1, "", MediaValidationFailure.ProbeRejectedMedia)]
    public async Task MapsProbeProcessFailuresByPhase(
        int termination,
        int exitCode,
        string standardError,
        MediaValidationFailure expectedFailure)
    {
        using var test = new ValidationTestContext();
        test.QueueSuccessfulPreflight();
        test.Runner.Enqueue(new BoundedProcessResult(
            (ProcessTermination)termination,
            exitCode,
            string.Empty,
            standardError));
        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.WorkingDirectory,
            test.Tools);

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.ValidateAsync(preflight));

        Assert.Equal(expectedFailure, exception.Failure);
    }

    [Theory]
    [InlineData((int)ProcessTermination.LaunchFailed, 0, "", MediaValidationFailure.DecodeLaunchFailed)]
    [InlineData((int)ProcessTermination.TimedOut, 0, "", MediaValidationFailure.DecodeTimedOut)]
    [InlineData((int)ProcessTermination.StandardErrorLimitExceeded, 0, "", MediaValidationFailure.DecodeOutputLimitExceeded)]
    [InlineData((int)ProcessTermination.Exited, 1, "damaged macroblock", MediaValidationFailure.CorruptMedia)]
    [InlineData((int)ProcessTermination.Exited, 1, "Decoding requested, but no decoder found", MediaValidationFailure.UnsupportedCodec)]
    [InlineData((int)ProcessTermination.Exited, 0, "", MediaValidationFailure.DecodeProgressMalformed)]
    public async Task MapsDecodeFailuresByPhase(
        int termination,
        int exitCode,
        string standardError,
        MediaValidationFailure expectedFailure)
    {
        using var test = new ValidationTestContext();
        test.QueueSuccessfulPreflight();
        test.Runner.Enqueue(Exited(ProbeJson(Video(0, true), Audio(1, true))));
        test.Runner.Enqueue(new BoundedProcessResult(
            (ProcessTermination)termination,
            exitCode,
            string.Empty,
            standardError));
        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.WorkingDirectory,
            test.Tools);

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.ValidateAsync(preflight));

        Assert.Equal(expectedFailure, exception.Failure);
    }

    [Fact]
    public async Task DetectsMediaIntegrityMismatchBeforeProbe()
    {
        using var test = new ValidationTestContext();
        test.QueueSuccessfulPreflight();
        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.WorkingDirectory,
            test.Tools);

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.ValidateAsync(
                test.Workspace.Layout,
                test.WorkingDirectory,
                test.MediaPath,
                new string('a', 64),
                test.MediaByteLength,
                test.Tools,
                preflight));

        Assert.Equal(MediaValidationFailure.IntegrityChanged, exception.Failure);
        Assert.Equal(2, test.Runner.Invocations.Count);
    }

    [Fact]
    public async Task RehashesToolImmediatelyBeforeMediaInvocation()
    {
        using var test = new ValidationTestContext();
        test.QueueSuccessfulPreflight();
        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.WorkingDirectory,
            test.Tools);
        await File.WriteAllBytesAsync(test.FfprobePath, [9, 9, 9]);

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.ValidateAsync(preflight));

        Assert.Equal(MediaValidationFailure.ToolIntegrityMismatch, exception.Failure);
        Assert.Equal(2, test.Runner.Invocations.Count);
    }

    [Fact]
    public async Task CancellationPropagatesAndReleasesMediaHandle()
    {
        using var test = new ValidationTestContext();
        test.QueueSuccessfulPreflight();
        test.Runner.Enqueue(Exited(ProbeJson(Video(0, true), Audio(1, true))));
        test.Runner.Enqueue((_, cancellationToken) =>
            Task.FromCanceled<BoundedProcessResult>(new CancellationToken(canceled: true)));
        var preflight = await test.Service.PreflightAsync(
            test.Workspace.Layout,
            test.WorkingDirectory,
            test.Tools);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.ValidateAsync(preflight));

        using var exclusive = new FileStream(
            test.MediaPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task PreflightMapsBoundedOutputFailureWithoutLeakingPaths()
    {
        using var test = new ValidationTestContext();
        test.Runner.Enqueue(new BoundedProcessResult(
            ProcessTermination.StandardOutputLimitExceeded,
            -1,
            "truncated",
            string.Empty));

        var exception = await Assert.ThrowsAsync<MediaValidationException>(() =>
            test.Service.PreflightAsync(
                test.Workspace.Layout,
                test.WorkingDirectory,
                test.Tools));

        Assert.Equal(MediaValidationFailure.ToolIdentityOutputLimitExceeded, exception.Failure);
        Assert.DoesNotContain(test.FfprobePath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static BoundedProcessResult Exited(string standardOutput) =>
        new(ProcessTermination.Exited, 0, standardOutput, string.Empty);

    private static string FfprobeIdentity(
        string version = ValidationTestContext.Version,
        string compiler = ValidationTestContext.Compiler,
        string configuration = ValidationTestContext.Configuration) =>
        JsonSerializer.Serialize(new
        {
            program_version = new
            {
                version,
                compiler_ident = compiler,
                configuration,
            },
        });

    private static string FfmpegIdentity(
        string version = ValidationTestContext.Version,
        string compiler = ValidationTestContext.Compiler,
        string configuration = ValidationTestContext.Configuration) =>
        $"ffmpeg version {version} Copyright test\n"
        + $"built with {compiler}\n"
        + $"configuration: {configuration}\n";

    private static string DecodeProgress(long microseconds) =>
        $"frame=30\nout_time_us={microseconds}\nprogress=end\n";

    private static string ProbeJson(params string[] streams) =>
        ProbeJsonWith("mov,mp4,m4a,3gp,3g2,mj2", "1.25", "isom", streams);

    private static string ProbeJsonWith(
        string formatName,
        string duration,
        string majorBrand,
        params string[] streams) =>
        $$"""
        {
          "format": {
            "format_name": "{{formatName}}",
            "duration": "{{duration}}",
            "tags": { "major_brand": "{{majorBrand}}" }
          },
          "streams": [{{string.Join(',', streams)}}]
        }
        """;

    private static string Video(
        int index,
        bool isDefault,
        string codec = "h264",
        int width = 1920,
        int height = 1080,
        int attachedPicture = 0) =>
        $$"""
        {
          "index": {{index}}, "codec_type": "video", "codec_name": "{{codec}}",
          "width": {{width}}, "height": {{height}},
          "avg_frame_rate": "30/1", "r_frame_rate": "30/1",
          "disposition": { "default": {{(isDefault ? 1 : 0)}}, "attached_pic": {{attachedPicture}} }
        }
        """;

    private static string Audio(
        int index,
        bool isDefault,
        string codec = "aac",
        string sampleRate = "48000",
        int channels = 2) =>
        $$"""
        {
          "index": {{index}}, "codec_type": "audio", "codec_name": "{{codec}}",
          "sample_rate": "{{sampleRate}}", "channels": {{channels}},
          "disposition": { "default": {{(isDefault ? 1 : 0)}}, "attached_pic": 0 }
        }
        """;

    private sealed class ValidationTestContext : IDisposable
    {
        public const string Version = "n8.1-test";
        public const string Compiler = "test-compiler";
        public const string Configuration = "--enable-test";

        public ValidationTestContext()
        {
            Workspace = new TestWorkspace();
            WorkingDirectory = Path.Combine(Workspace.Layout.ProcessingRoot, "validation-job");
            Directory.CreateDirectory(WorkingDirectory);

            var assetDirectory = Path.Combine(Workspace.Layout.MediaRoot, "asset");
            Directory.CreateDirectory(assetDirectory);
            MediaPath = Path.Combine(assetDirectory, "original.mp4");
            var mediaBytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
            File.WriteAllBytes(MediaPath, mediaBytes);
            MediaByteLength = mediaBytes.LongLength;
            MediaSha256 = Convert.ToHexStringLower(SHA256.HashData(mediaBytes));

            var toolsDirectory = Path.Combine(Workspace.Root, "tools");
            Directory.CreateDirectory(toolsDirectory);
            FfprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
            FfmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
            File.WriteAllBytes(FfprobePath, Encoding.UTF8.GetBytes("fake-ffprobe"));
            File.WriteAllBytes(FfmpegPath, Encoding.UTF8.GetBytes("fake-ffmpeg"));
            FfprobeSha256 = HashFile(FfprobePath);
            FfmpegSha256 = HashFile(FfmpegPath);

            Tools = new(
                new(FfprobePath, FfprobeSha256, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 1024 * 1024, 64 * 1024),
                new(FfmpegPath, FfmpegSha256, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), 1024 * 1024, 64 * 1024),
                "n8.1",
                MediaValidationService.CurrentValidationContractVersion);
            Service = new(Runner);
        }

        public TestWorkspace Workspace { get; }

        public string WorkingDirectory { get; }

        public string MediaPath { get; }

        public string MediaSha256 { get; }

        public long MediaByteLength { get; }

        public string FfprobePath { get; }

        public string FfmpegPath { get; }

        public string FfprobeSha256 { get; }

        public string FfmpegSha256 { get; }

        public MediaValidationToolContract Tools { get; }

        public FakeProcessRunner Runner { get; } = new();

        public MediaValidationService Service { get; }

        public void QueueSuccessfulPreflight()
        {
            Runner.Enqueue(Exited(FfprobeIdentity()));
            Runner.Enqueue(Exited(FfmpegIdentity()));
        }

        public Task<ValidatedMediaMetadata> ValidateAsync(MediaValidationPreflight preflight) =>
            Service.ValidateAsync(
                Workspace.Layout,
                WorkingDirectory,
                MediaPath,
                MediaSha256,
                MediaByteLength,
                Tools,
                preflight);

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

        public void Enqueue(
            Func<ProcessInvocation, CancellationToken, Task<BoundedProcessResult>> result) =>
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
            var invocation = new ProcessInvocation(
                executablePath,
                workingDirectoryPath,
                [.. arguments],
                timeout,
                maximumStandardOutputBytes,
                maximumStandardErrorBytes);
            Invocations.Add(invocation);
            return _results.Dequeue()(invocation, cancellationToken);
        }
    }

    private sealed record ProcessInvocation(
        string ExecutablePath,
        string WorkingDirectoryPath,
        IReadOnlyList<string> Arguments,
        TimeSpan Timeout,
        int MaximumStandardOutputBytes,
        int MaximumStandardErrorBytes);
}
