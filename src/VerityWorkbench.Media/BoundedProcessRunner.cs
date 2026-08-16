using System.Diagnostics;
using System.Text;

namespace VerityWorkbench.Media;

internal enum ProcessTermination
{
    Exited,
    LaunchFailed,
    TimedOut,
    StandardOutputLimitExceeded,
    StandardErrorLimitExceeded,
}

internal sealed record BoundedProcessResult(
    ProcessTermination Termination,
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal interface IBoundedProcessRunner
{
    Task<BoundedProcessResult> RunAsync(
        string executablePath,
        string workingDirectoryPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs an explicitly named executable directly. It never invokes a shell.
/// </summary>
internal sealed class BoundedProcessRunner : IBoundedProcessRunner
{
    public async Task<BoundedProcessResult> RunAsync(
        string executablePath,
        string workingDirectoryPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment.Remove("FFREPORT");
        startInfo.Environment["AV_LOG_FORCE_NOCOLOR"] = "1";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        WindowsProcessJob? processJob;
        try
        {
            processJob = WindowsProcessJob.Create();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(ProcessTermination.LaunchFailed, -1, string.Empty, string.Empty);
        }

        using (processJob)
        {
            try
            {
                if (!process.Start())
                {
                    return new(ProcessTermination.LaunchFailed, -1, string.Empty, string.Empty);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new(ProcessTermination.LaunchFailed, -1, string.Empty, string.Empty);
            }

            if (processJob is not null && !processJob.TryAssign(process))
            {
                if (!process.HasExited)
                {
                    TryKillProcessTree(process, processJob);
                    await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                    return new(ProcessTermination.LaunchFailed, -1, string.Empty, string.Empty);
                }
            }

            var stdoutExceeded = 0;
            var stderrExceeded = 0;
            using var outputLimitCancellation = new CancellationTokenSource();
            void KillForOutputLimit(ref int flag)
            {
                Interlocked.Exchange(ref flag, 1);
                TryKillProcessTree(process, processJob);
                outputLimitCancellation.Cancel();
            }

            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token,
                outputLimitCancellation.Token);
            // CancellationToken callbacks run synchronously with Cancel(). This makes
            // a window-close cancellation kill the process tree immediately instead
            // of depending on this method's async continuation being scheduled.
            using var cancellationRegistration = lifetimeCancellation.Token.Register(
                () => TryKillProcessTree(process, processJob));

            var stdoutTask = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                maximumStandardOutputBytes,
                () => KillForOutputLimit(ref stdoutExceeded),
                lifetimeCancellation.Token);
            var stderrTask = ReadBoundedAsync(
                process.StandardError.BaseStream,
                maximumStandardErrorBytes,
                () => KillForOutputLimit(ref stderrExceeded),
                lifetimeCancellation.Token);

            try
            {
                await process.WaitForExitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process, processJob);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                await ObserveReadersWithoutThrowingAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process, processJob);
                await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                await ObserveReadersWithoutThrowingAsync(stdoutTask, stderrTask).ConfigureAwait(false);

                if (Volatile.Read(ref stdoutExceeded) != 0)
                {
                    return new(ProcessTermination.StandardOutputLimitExceeded, -1, string.Empty, string.Empty);
                }

                if (Volatile.Read(ref stderrExceeded) != 0)
                {
                    return new(ProcessTermination.StandardErrorLimitExceeded, -1, string.Empty, string.Empty);
                }

                return new(ProcessTermination.TimedOut, -1, string.Empty, string.Empty);
            }

            var stdout = await ReadResultWithoutThrowingAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadResultWithoutThrowingAsync(stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (stdout?.LimitExceeded == true || Volatile.Read(ref stdoutExceeded) != 0)
            {
                return new(
                    ProcessTermination.StandardOutputLimitExceeded,
                    process.ExitCode,
                    stdout?.Text ?? string.Empty,
                    stderr?.Text ?? string.Empty);
            }

            if (stderr?.LimitExceeded == true || Volatile.Read(ref stderrExceeded) != 0)
            {
                return new(
                    ProcessTermination.StandardErrorLimitExceeded,
                    process.ExitCode,
                    stdout?.Text ?? string.Empty,
                    stderr?.Text ?? string.Empty);
            }

            if (timeoutCancellation.IsCancellationRequested)
            {
                return new(ProcessTermination.TimedOut, -1, string.Empty, string.Empty);
            }

            if (stdout is null || stderr is null)
            {
                return new(ProcessTermination.LaunchFailed, -1, string.Empty, string.Empty);
            }

            return new(ProcessTermination.Exited, process.ExitCode, stdout.Text, stderr.Text);
        }
    }

    private static async Task<BoundedReadResult> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        Action onLimitExceeded,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[maximumBytes];
        var totalRead = 0;
        var buffer = new byte[Math.Min(8192, maximumBytes + 1)];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maximumBytes - totalRead;
            if (read > remaining)
            {
                if (remaining > 0)
                {
                    buffer.AsSpan(0, remaining).CopyTo(bytes.AsSpan(totalRead));
                    totalRead += remaining;
                }

                onLimitExceeded();
                return new(Encoding.UTF8.GetString(bytes, 0, totalRead), true);
            }

            buffer.AsSpan(0, read).CopyTo(bytes.AsSpan(totalRead));
            totalRead += read;
        }

        return new(Encoding.UTF8.GetString(bytes, 0, totalRead), false);
    }

    private static void TryKillProcessTree(Process process, WindowsProcessJob? processJob)
    {
        processJob?.Dispose();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            // The process either exited concurrently or the platform could not
            // enumerate descendants. Disposing the Process still closes handles.
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        using var boundedWait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(boundedWait.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or OperationCanceledException)
        {
        }
    }

    private static async Task ObserveReadersWithoutThrowingAsync(params Task<BoundedReadResult>[] readers)
    {
        try
        {
            await Task.WhenAll(readers)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task<BoundedReadResult?> ReadResultWithoutThrowingAsync(
        Task<BoundedReadResult> reader)
    {
        try
        {
            return await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record BoundedReadResult(string Text, bool LimitExceeded);
}
