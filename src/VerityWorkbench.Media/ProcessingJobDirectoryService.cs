using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media;

/// <summary>
/// Inspects and deletes one recorded processing-job directory without following
/// links or accepting a caller-supplied absolute path.
/// </summary>
public sealed class ProcessingJobDirectoryService
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileDeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const int MaximumStoredPathLength = 1_024;
    private const int ShortIdLength = 12;

    private static readonly string[] PromotionJournalNames =
    [
        ".promotion-journal",
        ".preprocessing-promotion-journal",
    ];

    public ProcessingJobDirectoryInspection Inspect(
        ProfileWorkspaceLayout layout,
        Guid jobId,
        string workspaceRelativePath)
    {
        ValidateLayout(layout);
        if (jobId == Guid.Empty)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.JobIdInvalid,
                "The processing job ID is invalid.");
        }

        var target = ResolveCanonicalJobPath(layout, jobId, workspaceRelativePath);
        var targetAttributes = TryGetAttributes(target);
        if (targetAttributes is null)
        {
            return new(ProcessingJobDirectoryState.Missing, FullPath: null);
        }

        if ((targetAttributes.Value & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.ReparsePointDetected,
                "A reparse point is not allowed in a processing-job boundary.");
        }

        if ((targetAttributes.Value & FileAttributes.Directory) == 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.TargetNotDirectory,
                "The stored processing-job target is not a directory.");
        }

        EnsureTreeHasNoReparsePoints(target);
        EnsureRegularJobMarker(target);
        EnsureNoPendingPromotionEvidence(target);
        return new(ProcessingJobDirectoryState.Present, target);
    }

    public Task DeleteAsync(
        ProfileWorkspaceLayout layout,
        Guid jobId,
        string workspaceRelativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () =>
            {
                // Deliberately re-resolve and re-inspect from the stored relative
                // path in the same worker section as deletion. A prior UI
                // inspection is not accepted as authority to delete.
                var inspection = Inspect(layout, jobId, workspaceRelativePath);
                if (!inspection.IsPresent || inspection.FullPath is null)
                {
                    throw Failure(
                        ProcessingJobDirectoryFailure.Missing,
                        "The processing-job directory is missing.");
                }

                // Once mutation begins it is intentionally not cancellable, so
                // cancellation cannot manufacture a partial cleanup result.
                // Locks still surface as retryable IO/access failures, with the
                // .job marker deleted last.
                cancellationToken.ThrowIfCancellationRequested();
                if (OperatingSystem.IsWindows())
                {
                    DeleteWithWindowsDirectoryLeases(
                        layout,
                        jobId,
                        workspaceRelativePath,
                        inspection.FullPath,
                        cancellationToken);
                }
                else
                {
                    DeleteDirectoryContentsWithoutFollowingReparsePoints(
                        inspection.FullPath);
                    Directory.Delete(inspection.FullPath, recursive: false);
                }
            },
            cancellationToken);
    }

    private static void ValidateLayout(ProfileWorkspaceLayout layout)
    {
        if (layout is null)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.WorkspaceInvalid,
                "The profile workspace is invalid.");
        }

        var validation = WorkspacePathPolicy.Validate(layout.WorkspaceRoot);
        if (!validation.IsValid
            || !PathsEqual(validation.NormalizedPath!, layout.WorkspaceRoot)
            || !PathsEqual(
                layout.ProcessingRoot,
                Path.Combine(layout.WorkspaceRoot, "Processing")))
        {
            throw Failure(
                ProcessingJobDirectoryFailure.WorkspaceInvalid,
                "The profile workspace is invalid.");
        }

        var workspaceAttributes = TryGetAttributes(layout.WorkspaceRoot);
        var processingAttributes = TryGetAttributes(layout.ProcessingRoot);
        if (workspaceAttributes is null
            || processingAttributes is null
            || (workspaceAttributes.Value & FileAttributes.Directory) == 0
            || (processingAttributes.Value & FileAttributes.Directory) == 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.WorkspaceInvalid,
                "Initialize the profile workspace before inspecting processing jobs.");
        }

        if ((workspaceAttributes.Value & FileAttributes.ReparsePoint) != 0
            || (processingAttributes.Value & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.ReparsePointDetected,
                "A reparse point is not allowed in a processing-job boundary.");
        }
    }

    private static string ResolveCanonicalJobPath(
        ProfileWorkspaceLayout layout,
        Guid jobId,
        string workspaceRelativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRelativePath)
            || workspaceRelativePath.Length > MaximumStoredPathLength
            || workspaceRelativePath.Any(char.IsControl)
            || Path.IsPathFullyQualified(workspaceRelativePath)
            || workspaceRelativePath.Contains('\\'))
        {
            throw Failure(
                ProcessingJobDirectoryFailure.PathInvalid,
                "The stored processing-job path is invalid.");
        }

        var segments = workspaceRelativePath.Split('/', StringSplitOptions.None);
        var invalidFileNameCharacters = Path.GetInvalidFileNameChars();
        if (segments.Length != 2
            || !string.Equals(segments[0], "Processing", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(segments[1])
            || !string.Equals(segments[1], segments[1].Trim(), StringComparison.Ordinal)
            || segments[1] is "." or ".."
            || segments[1].EndsWith(".", StringComparison.Ordinal)
            || segments[1].IndexOfAny(invalidFileNameCharacters) >= 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.PathInvalid,
                "The stored processing-job path is not canonical.");
        }

        var shortJobId = jobId.ToString("N")[..ShortIdLength];
        if (!segments[1].EndsWith(shortJobId, StringComparison.Ordinal))
        {
            throw Failure(
                ProcessingJobDirectoryFailure.JobIdMismatch,
                "The processing-job directory does not match its stored job ID.");
        }

        string target;
        try
        {
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
                layout.WorkspaceRoot,
                "Processing",
                segments[1])));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.PathInvalid,
                "The stored processing-job path is invalid.");
        }

        var parent = Directory.GetParent(target);
        if (parent is null
            || !PathsEqual(parent.FullName, layout.ProcessingRoot)
            || !IsContained(layout.ProcessingRoot, target))
        {
            throw Failure(
                ProcessingJobDirectoryFailure.PathInvalid,
                "The stored processing-job path escapes Processing.");
        }

        return target;
    }

    private static void EnsureTreeHasNoReparsePoints(string target)
    {
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(
                    ProcessingJobDirectoryFailure.ReparsePointDetected,
                    "A reparse point is not allowed in a processing-job boundary.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw Failure(
                        ProcessingJobDirectoryFailure.ReparsePointDetected,
                        "A reparse point is not allowed in a processing-job boundary.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void EnsureRegularJobMarker(string target)
    {
        var markerAttributes = TryGetAttributes(Path.Combine(target, ".job"));
        if (markerAttributes is null
            || (markerAttributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.MarkerInvalid,
                "The processing-job directory has no regular .job marker.");
        }
    }

    private static void EnsureNoPendingPromotionEvidence(string target)
    {
        foreach (var markerName in PromotionJournalNames)
        {
            var markerPath = Path.Combine(target, markerName);
            var attributes = TryGetAttributes(markerPath);
            if (attributes is null)
            {
                continue;
            }

            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(
                    ProcessingJobDirectoryFailure.ReparsePointDetected,
                    "A reparse point is not allowed in a processing-job boundary.");
            }

            var hasEvidence = (attributes.Value & FileAttributes.Directory) != 0
                ? Directory.EnumerateFileSystemEntries(markerPath).Any()
                : new FileInfo(markerPath).Length > 0;
            if (hasEvidence)
            {
                throw Failure(
                    ProcessingJobDirectoryFailure.PendingPromotionEvidence,
                    "Resolve pending promotion evidence before deleting this processing job.");
            }
        }
    }

    private void DeleteWithWindowsDirectoryLeases(
        ProfileWorkspaceLayout layout,
        Guid jobId,
        string workspaceRelativePath,
        string expectedTarget,
        CancellationToken cancellationToken)
    {
        // Open each boundary without FILE_SHARE_DELETE and without traversing a
        // reparse point in its final component. Acquiring from the outside in
        // pins each ancestor before resolving the next path component.
        using var workspaceLease = OpenWindowsDirectoryLease(layout.WorkspaceRoot);
        using var processingLease = OpenWindowsDirectoryLease(layout.ProcessingRoot);
        var targetLease = OpenWindowsDirectoryLease(expectedTarget);
        try
        {
            // The pre-lease inspection is only an early refusal. This is the
            // authoritative inspection, after workspace, Processing, and the
            // exact job root can no longer be renamed or substituted.
            var inspection = Inspect(layout, jobId, workspaceRelativePath);
            if (!inspection.IsPresent
                || inspection.FullPath is null
                || !PathsEqual(inspection.FullPath, expectedTarget))
            {
                throw Failure(
                    ProcessingJobDirectoryFailure.Missing,
                    "The processing-job directory is missing.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            DeleteDirectoryContentsWithoutFollowingReparsePoints(expectedTarget);
        }
        finally
        {
            // Keep the workspace and Processing ancestors leased, but the job
            // root itself must be released before its final non-recursive
            // removal. A substitution after release cannot make that final
            // operation recurse through a target.
            targetLease.Dispose();
        }

        Directory.Delete(expectedTarget, recursive: false);
    }

    private static void DeleteDirectoryContentsWithoutFollowingReparsePoints(
        string target)
    {
        EnsureDirectoryIsRegular(target);
        string? rootJobMarker = null;
        foreach (var entry in Directory.EnumerateFileSystemEntries(target))
        {
            // Keep verifying the root before each mutation. Nested deletion is
            // delegated to Directory.Delete(recursive: true), whose runtime
            // contract removes directory reparse points without recursing into
            // their targets. This avoids the check/use gap of a manual walk.
            EnsureDirectoryIsRegular(target);

            if (PathsEqual(entry, Path.Combine(target, ".job")))
            {
                rootJobMarker = entry;
                continue;
            }

            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(
                    ProcessingJobDirectoryFailure.ReparsePointDetected,
                    "A reparse point is not allowed in a processing-job boundary.");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        // The marker is deleted last so an ordinary locked file or directory
        // leaves the partially cleaned job recognizable and safe to retry.
        if (rootJobMarker is null)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.MarkerInvalid,
                "The processing-job directory has no regular .job marker.");
        }

        EnsureDirectoryIsRegular(target);
        var markerAttributes = File.GetAttributes(rootJobMarker);
        if ((markerAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.MarkerInvalid,
                "The processing-job directory has no regular .job marker.");
        }

        File.Delete(rootJobMarker);
    }

    internal static SafeFileHandle OpenWindowsDirectoryLease(string path)
    {
        var handle = CreateFileW(
            path,
            desiredAccess: FileDeleteAccess | FileReadAttributes,
            FileShare.Read,
            securityAttributes: IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            templateFile: IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        var inner = new Win32Exception(error);
        const string message =
            "The processing-job directory could not be leased for cleanup.";
        if (error == 5)
        {
            throw new UnauthorizedAccessException(message, inner);
        }

        throw new IOException(message, inner);
    }

    private static void EnsureDirectoryIsRegular(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.ReparsePointDetected,
                "A reparse point is not allowed in a processing-job boundary.");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw Failure(
                ProcessingJobDirectoryFailure.TargetNotDirectory,
                "The stored processing-job target is not a directory.");
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)));
        return !Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static ProcessingJobDirectoryException Failure(
        ProcessingJobDirectoryFailure failure,
        string message) => new(failure, message);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
