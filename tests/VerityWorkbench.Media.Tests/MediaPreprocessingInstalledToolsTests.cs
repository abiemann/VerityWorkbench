using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace VerityWorkbench.Media.Tests;

[Collection("Installed FFmpeg")]
public sealed class MediaPreprocessingInstalledToolsTests
{
    private const string ExpectedVersion = "n8.1.2-44-g7c533d0f86-20260815";
    private const string ExpectedFfprobeSha256 = "aaa354b9841d92b4fa5f60eaf58169055b5d9d3d0420ac553784523dfe312724";
    private const string ExpectedFfmpegSha256 = "fe6faee813ef5b4407f10db5c8f0cc50cee0b0a1a981f0b903567e2ebb7b92df";

    [Fact]
    [Trait("Category", "InstalledToolIntegration")]
    public async Task PreparesAndAtomicallyPromotesFrozenArtifactsWhenPinnedToolsAreInstalled()
    {
        var toolRoot = Environment.GetEnvironmentVariable("VERITYWORKBENCH_FFMPEG_ROOT");
        if (string.IsNullOrWhiteSpace(toolRoot))
        {
            return;
        }

        var ffprobePath = Path.Combine(toolRoot, "bin", "ffprobe.exe");
        var ffmpegPath = Path.Combine(toolRoot, "bin", "ffmpeg.exe");
        if (!File.Exists(ffprobePath) || !File.Exists(ffmpegPath))
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var jobDirectory = Path.Combine(workspace.Layout.ProcessingRoot, "installed-preprocessing-job");
        var assetDirectory = Path.Combine(
            workspace.Layout.MediaRoot,
            "generated_" + assetId.ToString("N")[..12]);
        Directory.CreateDirectory(jobDirectory);
        Directory.CreateDirectory(assetDirectory);
        var sourcePath = Path.Combine(assetDirectory, "original.mp4");
        await GenerateOffsetVariableFrameRateMp4Async(ffmpegPath, sourcePath, jobDirectory);

        var sourceLength = new FileInfo(sourcePath).Length;
        var sourceSha256 = await HashFileAsync(sourcePath);
        var tools = CreateTools(ffprobePath, ffmpegPath);
        var validationService = new MediaValidationService();
        var preflight = await validationService.PreflightAsync(
            workspace.Layout,
            jobDirectory,
            tools);
        var validation = await validationService.ValidateAsync(
            workspace.Layout,
            jobDirectory,
            sourcePath,
            sourceSha256,
            sourceLength,
            tools,
            preflight);

        var service = new MediaPreprocessingService();
        var staged = await service.PrepareAsync(
            workspace.Layout,
            jobDirectory,
            new(jobId, assetId, sourcePath, sourceSha256, sourceLength, validation),
            tools,
            preflight);

        Assert.Equal("mp4", staged.Output.ProxyContainerFormat);
        Assert.Equal("mpeg4", staged.Output.ProxyVideoCodec);
        Assert.Equal("yuv420p", staged.Output.ProxyPixelFormat);
        Assert.Equal(30, staged.Output.ProxyFrameRateNumerator);
        Assert.Equal(1, staged.Output.ProxyFrameRateDenominator);
        Assert.Equal("aac", staged.Output.ProxyAudioCodec);
        Assert.Equal(48_000, staged.Output.ProxyAudioSampleRateHz);
        Assert.Equal(2, staged.Output.ProxyAudioChannelCount);
        Assert.Equal("pcm_s16le", staged.Output.AnalysisAudioCodec);
        Assert.Equal(16_000, staged.Output.AnalysisAudioSampleRateHz);
        Assert.Equal(1, staged.Output.AnalysisAudioChannelCount);
        Assert.True(staged.Output.AnalysisAudioSampleCount > 0);
        Assert.True(staged.Output.SourceTimelineOriginMicroseconds > 0);
        Assert.Equal(MediaPreprocessingService.NotAssessed, staged.Output.MediaQualityState);
        Assert.Equal(MediaPreprocessingService.NotAssessed, staged.Output.ModelApplicabilityState);
        Assert.Equal(4, Directory.EnumerateFiles(staged.StagedOutputDirectoryPath).Count());
        var manifestText = await File.ReadAllTextAsync(
            Path.Combine(staged.StagedOutputDirectoryPath, "preprocessing-manifest.json"));
        Assert.DoesNotContain(workspace.Root, manifestText, StringComparison.OrdinalIgnoreCase);
        using (var manifest = JsonDocument.Parse(manifestText))
        {
            var observation = manifest.RootElement
                .GetProperty("timeline")
                .GetProperty("variableFrameRateObservation");
            Assert.True(observation.GetProperty("variableIntervals").GetInt64() > 0);
        }

        var promoted = await service.PromoteAsync(workspace.Layout, staged);
        var verification = await service.VerifyPreparedAsync(workspace.Layout, promoted.Output);
        Assert.True(verification.IsValid, verification.FailureReason);
        Assert.Contains(
            Path.Combine("Prepared", "v1_" + promoted.Output.PreprocessingContractSha256[..12]),
            promoted.PreparedDirectoryPath,
            StringComparison.OrdinalIgnoreCase);
        service.ConfirmPromotion(workspace.Layout, promoted);
    }

    private static MediaValidationToolContract CreateTools(string ffprobePath, string ffmpegPath) =>
        new(
            new(
                ffprobePath,
                ExpectedFfprobeSha256,
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMinutes(2),
                8 * 1024 * 1024,
                1024 * 1024),
            new(
                ffmpegPath,
                ExpectedFfmpegSha256,
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMinutes(2),
                8 * 1024 * 1024,
                1024 * 1024),
            ExpectedVersion,
            MediaValidationService.CurrentValidationContractVersion);

    private static async Task GenerateOffsetVariableFrameRateMp4Async(
        string ffmpegPath,
        string outputPath,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string[] arguments =
        [
            "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=1.2",
            "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=1.0",
            "-filter_complex",
            "[0:v]select='not(mod(n,5)) + not(mod(n,2))',setpts=PTS+0.250/TB[v];[1:a]asetpts=PTS+0.500/TB[a]",
            "-map", "[v]", "-map", "[a]",
            "-fps_mode", "vfr",
            "-c:v", "mpeg4", "-q:v", "5",
            "-c:a", "aac",
            "-f", "mp4", outputPath,
        ];
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The installed FFmpeg fixture process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var errorText = await stderr;
        _ = await stdout;
        Assert.True(process.ExitCode == 0, $"Fixture generation failed: {errorText}");
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}
