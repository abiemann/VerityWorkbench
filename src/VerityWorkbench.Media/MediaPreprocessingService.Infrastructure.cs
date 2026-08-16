using System.Security.Cryptography;
using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media;

public sealed partial class MediaPreprocessingService
{
    private static void ValidateRequest(MediaPreprocessingRequest request)
    {
        if (request.JobId == Guid.Empty || request.MediaAssetId == Guid.Empty)
        {
            throw new ArgumentException("The preprocessing job and media asset IDs are required.", nameof(request));
        }

        if (!IsLowercaseSha256(request.ExpectedSourceSha256)
            || request.ExpectedSourceByteLength <= 0)
        {
            throw Failure(
                MediaPreprocessingFailure.SourceIntegrityInvalid,
                "The expected source integrity metadata is invalid.");
        }

        if (request.Validation is null
            || request.Validation.Video.StreamIndex < 0
            || request.Validation.Audio.StreamIndex < 0
            || request.Validation.Video.StreamIndex == request.Validation.Audio.StreamIndex
            || request.Validation.DurationMicroseconds <= 0
            || !IsLowercaseSha256(request.Validation.ValidationContractSha256))
        {
            throw new ArgumentException("The normalized media validation is invalid.", nameof(request));
        }
    }

    private static void ValidateLayout(ProfileWorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var validation = WorkspacePathPolicy.Validate(layout.WorkspaceRoot);
        if (!validation.IsValid || !PathsEqual(validation.NormalizedPath!, layout.WorkspaceRoot))
        {
            throw Failure(MediaPreprocessingFailure.WorkspaceInvalid, "The profile workspace is invalid.");
        }

        if (!Directory.Exists(layout.WorkspaceRoot)
            || !Directory.Exists(layout.ProcessingRoot)
            || !Directory.Exists(layout.MediaRoot)
            || !PathsEqual(layout.ProcessingRoot, Path.Combine(layout.WorkspaceRoot, "Processing"))
            || !PathsEqual(layout.MediaRoot, Path.Combine(layout.WorkspaceRoot, "Media")))
        {
            throw Failure(MediaPreprocessingFailure.WorkspaceInvalid, "Initialize the profile workspace before preprocessing media.");
        }

        EnsurePathSegmentsHaveNoReparsePoints(layout.WorkspaceRoot, layout.ProcessingRoot);
        EnsurePathSegmentsHaveNoReparsePoints(layout.WorkspaceRoot, layout.MediaRoot);
    }

    private static string ValidateJobDirectory(
        ProfileWorkspaceLayout layout,
        string processingJobDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(processingJobDirectoryPath)
            || !Path.IsPathFullyQualified(processingJobDirectoryPath))
        {
            throw Failure(MediaPreprocessingFailure.ProcessingPathInvalid, "The processing job path is invalid.");
        }

        var jobDirectory = RequireContainedPath(
            layout.ProcessingRoot,
            processingJobDirectoryPath,
            "The processing job path escapes Processing.");
        RequireDirectChild(
            layout.ProcessingRoot,
            jobDirectory,
            "The preprocessing job must be directly beneath Processing.");
        if (!Directory.Exists(jobDirectory))
        {
            throw Failure(MediaPreprocessingFailure.ProcessingPathInvalid, "The processing job directory does not exist.");
        }

        EnsurePathSegmentsHaveNoReparsePoints(layout.ProcessingRoot, jobDirectory);
        return jobDirectory;
    }

    private static string ValidateSourcePath(
        ProfileWorkspaceLayout layout,
        string mediaFilePath,
        Guid assetId)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath) || !Path.IsPathFullyQualified(mediaFilePath))
        {
            throw Failure(MediaPreprocessingFailure.MediaPathInvalid, "The source media path is invalid.");
        }

        var sourcePath = RequireContainedPath(
            layout.MediaRoot,
            mediaFilePath,
            "The source media path escapes Media.");
        if (!File.Exists(sourcePath)
            || !string.Equals(Path.GetFileName(sourcePath), "original.mp4", StringComparison.Ordinal))
        {
            throw Failure(MediaPreprocessingFailure.MediaPathInvalid, "The immutable original.mp4 is missing.");
        }

        var assetDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw Failure(MediaPreprocessingFailure.MediaPathInvalid, "The source media has no asset directory.");
        RequireDirectChild(layout.MediaRoot, assetDirectory, "A media asset must be directly beneath Media.");
        if (!new DirectoryInfo(assetDirectory).Name.EndsWith(
                "_" + assetId.ToString("N")[..12],
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(MediaPreprocessingFailure.MediaPathInvalid, "The source media directory does not match its asset ID.");
        }

        EnsurePathSegmentsHaveNoReparsePoints(layout.MediaRoot, sourcePath);
        return sourcePath;
    }

    private static ValidatedTools ValidateTools(
        MediaValidationToolContract tools,
        MediaValidationPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Ffprobe is null
            || tools.Ffmpeg is null
            || !string.Equals(
                tools.ValidationContractVersion,
                MediaValidationService.CurrentValidationContractVersion,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(tools.ExpectedVersionPrefix)
            || !IsLowercaseSha256(preflight.ValidationContractSha256)
            || !IsValidProvenance(preflight.Ffprobe)
            || !IsValidProvenance(preflight.Ffmpeg)
            || !string.Equals(preflight.Ffprobe.Version, preflight.Ffmpeg.Version, StringComparison.Ordinal)
            || !preflight.Ffprobe.Version.StartsWith(tools.ExpectedVersionPrefix, StringComparison.Ordinal)
            || !string.Equals(
                preflight.ValidationContractSha256,
                preflight.ValidationContractSha256.ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            throw Failure(MediaPreprocessingFailure.PreflightMismatch, "The pinned media-tool preflight is incompatible.");
        }

        var ffprobe = ValidateExecutable(tools.Ffprobe);
        var ffmpeg = ValidateExecutable(tools.Ffmpeg);
        if (!string.Equals(ffprobe.ExpectedSha256, preflight.Ffprobe.ExecutableSha256, StringComparison.Ordinal)
            || !string.Equals(ffmpeg.ExpectedSha256, preflight.Ffmpeg.ExecutableSha256, StringComparison.Ordinal))
        {
            throw Failure(MediaPreprocessingFailure.PreflightMismatch, "The media-tool preflight does not match the configured executables.");
        }

        return new(ffprobe, ffmpeg);
    }

    private static ValidatedExecutable ValidateExecutable(MediaValidationExecutableContract executable)
    {
        if (string.IsNullOrWhiteSpace(executable.ExecutablePath)
            || !Path.IsPathFullyQualified(executable.ExecutablePath)
            || !File.Exists(executable.ExecutablePath)
            || !IsLowercaseSha256(executable.ExpectedSha256)
            || executable.InvocationTimeout <= TimeSpan.Zero
            || executable.InvocationTimeout > TimeSpan.FromDays(1)
            || executable.MaximumStandardOutputBytes <= 0
            || executable.MaximumStandardOutputBytes > 16 * 1024 * 1024
            || executable.MaximumStandardErrorBytes <= 0
            || executable.MaximumStandardErrorBytes > 16 * 1024 * 1024)
        {
            throw Failure(MediaPreprocessingFailure.ToolContractInvalid, "A media-tool contract is invalid.");
        }

        var path = Path.GetFullPath(executable.ExecutablePath);
        EnsureNotReparsePoint(path);
        return new(
            path,
            executable.ExpectedSha256,
            executable.InvocationTimeout,
            executable.MaximumStandardOutputBytes,
            executable.MaximumStandardErrorBytes);
    }

    private static bool IsValidProvenance(MediaValidationToolProvenance provenance) =>
        provenance is not null
        && !string.IsNullOrWhiteSpace(provenance.Version)
        && !string.IsNullOrWhiteSpace(provenance.CompilerIdentifier)
        && !string.IsNullOrWhiteSpace(provenance.Configuration)
        && IsLowercaseSha256(provenance.ConfigurationSha256)
        && IsLowercaseSha256(provenance.ExecutableSha256);

    private static FileStream OpenSourceReadLock(string sourcePath)
    {
        try
        {
            return new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(MediaPreprocessingFailure.SourceIntegrityChanged, "The immutable source media could not be locked.");
        }
    }

    private static FileStream OpenExecutableReadLock(string executablePath)
    {
        try
        {
            return new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(MediaPreprocessingFailure.ToolIntegrityMismatch, "A pinned media tool could not be locked.");
        }
    }

    private static async Task VerifyExecutableIntegrityAsync(
        ValidatedExecutable executable,
        FileStream readLock,
        CancellationToken cancellationToken)
    {
        var current = await HashOpenFileAsync(readLock, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, executable.ExpectedSha256, StringComparison.Ordinal))
        {
            throw Failure(MediaPreprocessingFailure.ToolIntegrityMismatch, "A pinned media tool changed.");
        }
    }

    private static async Task VerifyIntegrityAsync(
        FileStream readLock,
        string expectedSha256,
        long expectedByteLength,
        CancellationToken cancellationToken)
    {
        if (readLock.Length != expectedByteLength)
        {
            throw Failure(MediaPreprocessingFailure.SourceIntegrityChanged, "The immutable source media changed length.");
        }

        var hash = await HashOpenFileAsync(readLock, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, expectedSha256, StringComparison.Ordinal))
        {
            throw Failure(MediaPreprocessingFailure.SourceIntegrityChanged, "The immutable source media changed hash.");
        }
    }

    private static async Task VerifyCurrentSourceIntegrityAsync(
        ProfileWorkspaceLayout layout,
        string sourcePath,
        FileStream readLock,
        string expectedSha256,
        long expectedByteLength,
        CancellationToken cancellationToken)
    {
        _ = ValidateSourcePathFromExisting(layout, sourcePath);
        await VerifyIntegrityAsync(
                readLock,
                expectedSha256,
                expectedByteLength,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ValidateSourcePathFromExisting(ProfileWorkspaceLayout layout, string sourcePath)
    {
        var bounded = RequireContainedPath(layout.MediaRoot, sourcePath, "The source media path escapes Media.");
        if (!File.Exists(bounded) || !string.Equals(Path.GetFileName(bounded), "original.mp4", StringComparison.Ordinal))
        {
            throw Failure(MediaPreprocessingFailure.SourceIntegrityChanged, "The immutable source media is missing.");
        }

        EnsurePathSegmentsHaveNoReparsePoints(layout.MediaRoot, bounded);
        return bounded;
    }

    private static async Task<string> HashOpenFileAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexStringLower(digest);
    }

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A relative path is required.", nameof(path));
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment =>
                segment is "." or ".."
                || string.IsNullOrWhiteSpace(segment)
                || segment.Contains(':')
                || segment.Any(char.IsControl)))
        {
            throw new ArgumentException("A relative path contains an invalid segment.", nameof(path));
        }

        return string.Join('/', segments);
    }

    private static void EnsurePathSegmentsHaveNoReparsePoints(string root, string target)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedTarget = RequireContainedPath(normalizedRoot, target, "The path escapes its boundary.");
        EnsureNotReparsePoint(normalizedRoot);
        if (PathsEqual(normalizedRoot, normalizedTarget))
        {
            return;
        }

        var current = normalizedRoot;
        foreach (var segment in Path.GetRelativePath(normalizedRoot, normalizedTarget).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current))
            {
                EnsureNotReparsePoint(current);
            }
        }
    }

    private static void EnsureTreeHasNoReparsePoints(string root)
    {
        if (!DirectoryExistsOrThrows(root))
        {
            throw new DirectoryNotFoundException("A required preprocessing directory is missing.");
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            EnsureNotReparsePoint(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Reparse points are not allowed in preprocessing boundaries.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static bool DirectoryExistsOrThrows(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool FileExistsOrThrows(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) == 0;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Reparse points are not allowed in preprocessing boundaries.");
        }
    }

    private static string RequireContainedPath(string root, string candidate, string error)
    {
        string normalizedRoot;
        string normalizedCandidate;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException(error, exception);
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        if (Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException(error);
        }

        return normalizedCandidate;
    }

    private static void RequireDirectChild(string parent, string child, string error)
    {
        var actualParent = Directory.GetParent(Path.TrimEndingDirectorySeparator(child));
        if (actualParent is null || !PathsEqual(parent, actualParent.FullName))
        {
            throw new IOException(error);
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static MediaPreprocessingException Failure(
        MediaPreprocessingFailure failure,
        string message) => new(failure, message);
}
