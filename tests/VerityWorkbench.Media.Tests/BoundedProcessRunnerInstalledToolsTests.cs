using System.Diagnostics;

namespace VerityWorkbench.Media.Tests;

[Collection("Installed FFmpeg")]
public sealed class BoundedProcessRunnerInstalledToolsTests
{
    [Fact]
    [Trait("Category", "InstalledToolIntegration")]
    public async Task EnforcesStandardOutputLimit()
    {
        var ffmpegPath = FindInstalledFfmpeg();
        if (ffmpegPath is null)
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var workingDirectory = Path.Combine(workspace.Layout.ProcessingRoot, "output-limit-job");
        Directory.CreateDirectory(workingDirectory);

        var result = await new BoundedProcessRunner().RunAsync(
            ffmpegPath,
            workingDirectory,
            ["-version"],
            TimeSpan.FromSeconds(5),
            maximumStandardOutputBytes: 16,
            maximumStandardErrorBytes: 1024,
            CancellationToken.None);

        Assert.Equal(ProcessTermination.StandardOutputLimitExceeded, result.Termination);
        Assert.True(result.StandardOutput.Length <= 16);
        await DeleteWorkingDirectoryWhenReleasedAsync(workingDirectory);
    }

    [Fact]
    [Trait("Category", "InstalledToolIntegration")]
    public async Task CancellationKillsLongRunningProcessAndReturnsPromptly()
    {
        var ffmpegPath = FindInstalledFfmpeg();
        if (ffmpegPath is null)
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var workingDirectory = Path.Combine(workspace.Layout.ProcessingRoot, "cancellation-job");
        Directory.CreateDirectory(workingDirectory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BoundedProcessRunner().RunAsync(
                ffmpegPath,
                workingDirectory,
                LongRunningArguments,
                TimeSpan.FromSeconds(10),
                maximumStandardOutputBytes: 1024,
                maximumStandardErrorBytes: 1024,
                cancellation.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workingDirectory));
        await DeleteWorkingDirectoryWhenReleasedAsync(workingDirectory);
    }

    [Fact]
    [Trait("Category", "InstalledToolIntegration")]
    public async Task TimeoutKillsLongRunningProcessAndReturnsTypedTermination()
    {
        var ffmpegPath = FindInstalledFfmpeg();
        if (ffmpegPath is null)
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var workingDirectory = Path.Combine(workspace.Layout.ProcessingRoot, "timeout-job");
        Directory.CreateDirectory(workingDirectory);
        var stopwatch = Stopwatch.StartNew();

        var result = await new BoundedProcessRunner().RunAsync(
            ffmpegPath,
            workingDirectory,
            LongRunningArguments,
            TimeSpan.FromMilliseconds(250),
            maximumStandardOutputBytes: 1024,
            maximumStandardErrorBytes: 1024,
            CancellationToken.None);

        Assert.Equal(ProcessTermination.TimedOut, result.Termination);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workingDirectory));
        await DeleteWorkingDirectoryWhenReleasedAsync(workingDirectory);
    }

    private static readonly IReadOnlyList<string> LongRunningArguments =
    [
        "-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i",
        "testsrc2=size=64x64:rate=1", "-t", "30", "-f", "null", "-",
    ];

    private static string? FindInstalledFfmpeg()
    {
        var toolRoot = Environment.GetEnvironmentVariable("VERITYWORKBENCH_FFMPEG_ROOT");
        if (string.IsNullOrWhiteSpace(toolRoot))
        {
            return null;
        }

        var path = Path.Combine(toolRoot, "bin", "ffmpeg.exe");
        return File.Exists(path) ? path : null;
    }

    private static async Task DeleteWorkingDirectoryWhenReleasedAsync(string workingDirectory)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                Directory.Delete(workingDirectory);
                return;
            }
            catch (IOException) when (attempt < 39)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }
    }
}
