using System.Diagnostics;
using System.Security.Cryptography;

namespace VerityWorkbench.Media.Tests;

[Collection("Installed FFmpeg")]
public sealed class MediaValidationInstalledToolsTests
{
    private const string ExpectedVersion = "n8.1.2-44-g7c533d0f86-20260815";
    private const string ExpectedFfprobeSha256 = "aaa354b9841d92b4fa5f60eaf58169055b5d9d3d0420ac553784523dfe312724";
    private const string ExpectedFfmpegSha256 = "fe6faee813ef5b4407f10db5c8f0cc50cee0b0a1a981f0b903567e2ebb7b92df";

    [Fact]
    [Trait("Category", "InstalledToolIntegration")]
    public async Task ValidatesGeneratedMp4WhenPinnedToolsAreInstalled()
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
        var jobDirectory = Path.Combine(workspace.Layout.ProcessingRoot, "installed-tool-validation");
        var assetDirectory = Path.Combine(workspace.Layout.MediaRoot, "generated-asset");
        Directory.CreateDirectory(jobDirectory);
        Directory.CreateDirectory(assetDirectory);
        var mediaPath = Path.Combine(assetDirectory, "original.mp4");

        await GenerateShortMp4Async(ffmpegPath, mediaPath, jobDirectory);
        var mediaLength = new FileInfo(mediaPath).Length;
        var mediaSha256 = await HashFileAsync(mediaPath);
        var tools = new MediaValidationToolContract(
            new(
                ffprobePath,
                ExpectedFfprobeSha256,
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                4 * 1024 * 1024,
                256 * 1024),
            new(
                ffmpegPath,
                ExpectedFfmpegSha256,
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                4 * 1024 * 1024,
                256 * 1024),
            ExpectedVersion,
            MediaValidationService.CurrentValidationContractVersion);
        var service = new MediaValidationService();

        var preflight = await service.PreflightAsync(
            workspace.Layout,
            jobDirectory,
            tools);
        var result = await service.ValidateAsync(
            workspace.Layout,
            jobDirectory,
            mediaPath,
            mediaSha256,
            mediaLength,
            tools,
            preflight);

        Assert.Equal("mp4", result.ContainerFormat);
        Assert.True(result.DecodeCompleted);
        Assert.True(result.DurationMicroseconds > 0);
        Assert.True(result.DecodedDurationMicroseconds > 0);
        Assert.Equal(ExpectedVersion, result.Ffprobe.Version);
        Assert.Equal(result.Ffprobe.Version, result.Ffmpeg.Version);
        Assert.Empty(Directory.EnumerateFiles(jobDirectory));

        var renamedThreeGpDirectory = Path.Combine(workspace.Layout.MediaRoot, "renamed-3gp");
        Directory.CreateDirectory(renamedThreeGpDirectory);
        var renamedThreeGpPath = Path.Combine(renamedThreeGpDirectory, "original.mp4");
        await GenerateShortMp4Async(
            ffmpegPath,
            renamedThreeGpPath,
            jobDirectory,
            outputFormat: "3gp");
        var renamedThreeGpSha256 = await HashFileAsync(renamedThreeGpPath);
        var threeGpException = await Assert.ThrowsAsync<MediaValidationException>(() =>
            service.ValidateAsync(
                workspace.Layout,
                jobDirectory,
                renamedThreeGpPath,
                renamedThreeGpSha256,
                new FileInfo(renamedThreeGpPath).Length,
                tools,
                preflight));
        Assert.Equal(MediaValidationFailure.UnsupportedContainer, threeGpException.Failure);
    }

    private static async Task GenerateShortMp4Async(
        string ffmpegPath,
        string outputPath,
        string workingDirectory,
        string outputFormat = "mp4")
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
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30",
            "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
            "-t", "0.5", "-c:v", "mpeg4", "-q:v", "5",
            "-c:a", "aac", "-shortest", "-f", outputFormat, outputPath,
        ];
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The installed FFmpeg test process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var errorText = await stderr;
        _ = await stdout;
        Assert.True(process.ExitCode == 0, $"Test MP4 generation failed: {errorText}");
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}
