using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media;

/// <summary>
/// Copies explicitly selected local MP4 files into a bounded processing job.
/// Staging never writes to Media; promotion is a separate atomic directory move.
/// </summary>
public sealed class LocalMediaStagingService
{
    private const int CopyBufferSize = 128 * 1024;
    private const int ShortIdLength = 12;
    private const int PromotionJournalVersion = 1;
    private const long MaximumPromotionJournalBytes = 64 * 1024;

    public static string BuildJobRelativePath(Guid jobId, DateTimeOffset createdAtUtc)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("The processing job ID cannot be empty.", nameof(jobId));
        }

        var utc = createdAtUtc.ToUniversalTime();
        var directoryName = string.Create(
            CultureInfo.InvariantCulture,
            $"{utc:yyyyMMdd'T'HHmmssfffffff'Z'}_local-media_{ShortId(jobId)}");

        return Path.Combine("Processing", directoryName);
    }

    public async Task<LocalMediaStagingJobResult> StageAsync(
        ProfileWorkspaceLayout layout,
        Guid jobId,
        DateTimeOffset createdAtUtc,
        IReadOnlyCollection<LocalMediaStageRequest> requests,
        IProgress<LocalMediaStagingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            throw new ArgumentException("At least one local MP4 is required.", nameof(requests));
        }

        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("The processing job ID cannot be empty.", nameof(jobId));
        }

        var validatedRequests = ValidateRequests(requests);
        cancellationToken.ThrowIfCancellationRequested();

        var createdUtc = createdAtUtc.ToUniversalTime();
        var jobRelativePath = BuildJobRelativePath(jobId, createdUtc);
        var jobDirectoryPath = RequireContainedPath(
            layout.WorkspaceRoot,
            Path.Combine(layout.WorkspaceRoot, jobRelativePath),
            "The processing job path escapes the profile workspace.");

        RequireDirectChild(layout.ProcessingRoot, jobDirectoryPath, "The processing job must be directly beneath Processing.");
        EnsurePathSegmentsHaveNoReparsePoints(layout.WorkspaceRoot, layout.ProcessingRoot);

        if (Directory.Exists(jobDirectoryPath) || File.Exists(jobDirectoryPath))
        {
            throw new IOException("The processing job directory already exists.");
        }

        Directory.CreateDirectory(jobDirectoryPath);
        EnsureNotReparsePoint(jobDirectoryPath);

        // CreateNew provides an exclusive job claim if two callers race to create
        // the same deterministic job directory. It intentionally contains no paths.
        var claimPath = Path.Combine(jobDirectoryPath, ".job");
        await using (var claim = new FileStream(
                         claimPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 1,
                         FileOptions.Asynchronous))
        {
            await claim.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var itemsRoot = Path.Combine(jobDirectoryPath, "items");
        Directory.CreateDirectory(itemsRoot);
        EnsureNotReparsePoint(itemsRoot);

        var stagedItems = new List<StagedLocalMediaItem>(validatedRequests.Count);
        for (var index = 0; index < validatedRequests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = validatedRequests[index];
            var item = await StageOneAsync(
                    jobId,
                    request,
                    index + 1,
                    validatedRequests.Count,
                    itemsRoot,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            stagedItems.Add(item);
        }

        return new(
            jobId,
            createdUtc,
            jobRelativePath,
            jobDirectoryPath,
            stagedItems.AsReadOnly());
    }

    /// <summary>
    /// Verifies a completed staged item, then atomically moves its directory into
    /// Media. Labels affect the readable folder name only; assetId is its identity.
    /// </summary>
    public async Task<PromotedLocalMediaAsset> PromoteAsync(
        ProfileWorkspaceLayout layout,
        StagedLocalMediaItem stagedItem,
        string? recordingLabel,
        string? safeSourceName,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(stagedItem);

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("The media asset ID cannot be empty.", nameof(assetId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stagedDirectory = ValidateStagedItemLocation(layout, stagedItem);
        EnsureTreeHasNoReparsePoints(stagedDirectory);

        if (Directory.EnumerateFiles(stagedDirectory, "*.part", SearchOption.AllDirectories).Any())
        {
            throw new IOException("A directory containing partial files cannot be promoted.");
        }

        var expectedOriginalPath = Path.Combine(stagedDirectory, "original.mp4");
        var stagedFile = Path.GetFullPath(stagedItem.StagedFilePath);
        if (!PathsEqual(expectedOriginalPath, stagedFile) || !File.Exists(stagedFile))
        {
            throw new IOException("The completed staged original.mp4 is missing or has an invalid path.");
        }

        var info = new FileInfo(stagedFile);
        if (info.Length != stagedItem.ByteLength)
        {
            throw new IOException("The staged media length changed before promotion.");
        }

        var currentHash = await ComputeSha256Async(stagedFile, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, stagedItem.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The staged media hash changed before promotion.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureTreeHasNoReparsePoints(layout.MediaRoot);

        var readableName = BuildReadableAssetDirectoryName(recordingLabel, safeSourceName, assetId);
        var destinationDirectory = RequireContainedPath(
            layout.MediaRoot,
            Path.Combine(layout.MediaRoot, readableName),
            "The media asset path escapes Media.");
        RequireDirectChild(layout.MediaRoot, destinationDirectory, "A media asset must be directly beneath Media.");

        if (Directory.Exists(destinationDirectory) || File.Exists(destinationDirectory))
        {
            throw new IOException("The destination media asset directory already exists.");
        }

        var journal = CreatePromotionJournal(
            layout,
            stagedItem,
            assetId,
            destinationDirectory);
        await WritePromotionJournalAsync(layout, journal, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(stagedDirectory, destinationDirectory);

        var originalPath = Path.Combine(destinationDirectory, "original.mp4");
        var workspaceRelativePath = Path.GetRelativePath(layout.WorkspaceRoot, originalPath)
            .Replace(Path.DirectorySeparatorChar, '/');

        return new(
            assetId,
            stagedItem.JobId,
            stagedItem.TrainingVideoId,
            currentHash,
            stagedItem.ByteLength,
            destinationDirectory,
            originalPath,
            workspaceRelativePath,
            stagedDirectory);
    }

    /// <summary>
    /// Atomically moves a just-promoted asset back to its originating processing
    /// item directory. No bytes are deleted. Call in reverse promotion order when
    /// a later database transaction fails.
    /// </summary>
    public void RollbackPromotion(
        ProfileWorkspaceLayout layout,
        PromotedLocalMediaAsset promotedAsset)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(promotedAsset);

        var assetDirectory = RequireContainedPath(
            layout.MediaRoot,
            promotedAsset.AssetDirectoryPath,
            "The promoted asset path escapes Media.");
        RequireDirectChild(layout.MediaRoot, assetDirectory, "A media asset must be directly beneath Media.");

        var expectedOriginal = Path.Combine(assetDirectory, "original.mp4");
        var expectedRelative = Path.GetRelativePath(layout.WorkspaceRoot, expectedOriginal)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!PathsEqual(expectedOriginal, promotedAsset.OriginalFilePath)
            || !string.Equals(expectedRelative, promotedAsset.WorkspaceRelativeOriginalPath, StringComparison.Ordinal)
            || !new DirectoryInfo(assetDirectory).Name.EndsWith(
                $"_{ShortId(promotedAsset.AssetId)}",
                StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(assetDirectory)
            || !File.Exists(expectedOriginal))
        {
            throw new IOException("The promoted media asset is missing or has an invalid path.");
        }

        EnsureTreeHasNoReparsePoints(assetDirectory);

        var stagedDirectory = RequireContainedPath(
            layout.ProcessingRoot,
            promotedAsset.OriginatingStagedDirectoryPath,
            "The rollback destination escapes Processing.");
        ValidateStagedDirectoryShape(layout, stagedDirectory, promotedAsset.TrainingVideoId, promotedAsset.JobId);
        EnsurePathSegmentsHaveNoReparsePoints(layout.ProcessingRoot, Path.GetDirectoryName(stagedDirectory)!);

        if (Directory.Exists(stagedDirectory) || File.Exists(stagedDirectory))
        {
            throw new IOException("The rollback destination already exists.");
        }

        Directory.Move(assetDirectory, stagedDirectory);
        DeletePromotionJournal(
            layout,
            promotedAsset.OriginatingStagedDirectoryPath,
            promotedAsset.JobId,
            promotedAsset.AssetId);
    }

    /// <summary>
    /// Clears the crash-recovery journal only after the corresponding SQLite
    /// asset/link transaction has committed.
    /// </summary>
    public void CommitPromotion(
        ProfileWorkspaceLayout layout,
        PromotedLocalMediaAsset promotedAsset)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(promotedAsset);
        _ = ValidateStoredAssetLocation(
            layout,
            promotedAsset.WorkspaceRelativeOriginalPath,
            promotedAsset.AssetId);
        DeletePromotionJournal(
            layout,
            promotedAsset.OriginatingStagedDirectoryPath,
            promotedAsset.JobId,
            promotedAsset.AssetId);
    }

    /// <summary>
    /// Confirms that a persisted immutable asset still exists at its bounded
    /// Media path with the recorded length and SHA-256 before it is reused.
    /// </summary>
    public async Task VerifyExistingAssetAsync(
        ProfileWorkspaceLayout layout,
        Guid assetId,
        string workspaceRelativeOriginalPath,
        string sha256,
        long byteLength,
        CancellationToken cancellationToken = default)
    {
        ValidateLayout(layout);
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("The media asset ID cannot be empty.", nameof(assetId));
        }

        if (!IsLowercaseSha256(sha256))
        {
            throw new ArgumentException("The expected SHA-256 is invalid.", nameof(sha256));
        }

        if (byteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        var originalPath = ValidateStoredAssetLocation(
            layout,
            workspaceRelativeOriginalPath,
            assetId);
        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException(
                "A persisted immutable media asset is missing from the workspace.",
                originalPath);
        }

        EnsurePathSegmentsHaveNoReparsePoints(layout.MediaRoot, originalPath);
        var fileInfo = new FileInfo(originalPath);
        if (fileInfo.Length != byteLength)
        {
            throw new InvalidDataException(
                "A persisted immutable media asset no longer has its recorded byte length.");
        }

        var currentHash = await ComputeSha256Async(originalPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash, sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A persisted immutable media asset no longer matches its recorded SHA-256.");
        }
    }

    /// <summary>
    /// Reconciles only journals whose jobs are known to be terminal or stale.
    /// Fresh jobs from another app window must not be included in eligibleJobIds.
    /// </summary>
    public async Task<LocalMediaPromotionReconciliationResult> ReconcilePendingPromotionsAsync(
        ProfileWorkspaceLayout layout,
        IReadOnlyDictionary<Guid, string> committedAssetPaths,
        IReadOnlySet<Guid> eligibleJobIds,
        CancellationToken cancellationToken = default)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(committedAssetPaths);
        ArgumentNullException.ThrowIfNull(eligibleJobIds);

        var completed = 0;
        var rolledBack = 0;
        var cleared = 0;
        var warnings = 0;

        foreach (var jobDirectory in Directory.EnumerateDirectories(layout.ProcessingRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureNotReparsePoint(jobDirectory);
                RequireDirectChild(
                    layout.ProcessingRoot,
                    jobDirectory,
                    "A processing job must be directly beneath Processing.");
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                warnings++;
                continue;
            }

            var journalDirectory = Path.Combine(jobDirectory, ".promotion-journal");
            if (!Directory.Exists(journalDirectory))
            {
                continue;
            }

            try
            {
                EnsureNotReparsePoint(journalDirectory);
                RequireDirectChild(
                    jobDirectory,
                    journalDirectory,
                    "A promotion journal has an invalid location.");
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                warnings++;
                continue;
            }

            foreach (var journalPath in Directory.EnumerateFiles(journalDirectory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var journal = await ReadPromotionJournalAsync(journalPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (!eligibleJobIds.Contains(journal.JobId))
                    {
                        continue;
                    }

                    ValidatePromotionJournalFileName(journalPath, journal.AssetId);
                    var stagedDirectory = ResolveStagedDirectoryFromJournal(layout, journal);
                    var assetOriginalPath = ValidateStoredAssetLocation(
                        layout,
                        journal.AssetOriginalRelativePath,
                        journal.AssetId);
                    var assetDirectory = Path.GetDirectoryName(assetOriginalPath)
                        ?? throw new InvalidDataException("A promotion journal has no asset directory.");
                    var stagedExists = Directory.Exists(stagedDirectory);
                    var assetExists = Directory.Exists(assetDirectory);

                    if (committedAssetPaths.TryGetValue(journal.AssetId, out var committedPath))
                    {
                        if (!string.Equals(
                                NormalizeRelativePath(committedPath),
                                journal.AssetOriginalRelativePath,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "A committed asset path does not match its promotion journal.");
                        }

                        if (assetExists && !stagedExists)
                        {
                            await VerifyExistingAssetAsync(
                                    layout,
                                    journal.AssetId,
                                    journal.AssetOriginalRelativePath,
                                    journal.Sha256,
                                    journal.ByteLength,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            File.Delete(journalPath);
                            completed++;
                            continue;
                        }

                        if (!assetExists && stagedExists)
                        {
                            await VerifyStagedJournalItemAsync(
                                    stagedDirectory,
                                    journal,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            Directory.Move(stagedDirectory, assetDirectory);
                            await VerifyExistingAssetAsync(
                                    layout,
                                    journal.AssetId,
                                    journal.AssetOriginalRelativePath,
                                    journal.Sha256,
                                    journal.ByteLength,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            File.Delete(journalPath);
                            completed++;
                            continue;
                        }

                        warnings++;
                        continue;
                    }

                    if (assetExists && !stagedExists)
                    {
                        var promoted = new PromotedLocalMediaAsset(
                            journal.AssetId,
                            journal.JobId,
                            journal.TrainingVideoId,
                            journal.Sha256,
                            journal.ByteLength,
                            assetDirectory,
                            assetOriginalPath,
                            journal.AssetOriginalRelativePath,
                            stagedDirectory);
                        RollbackPromotion(layout, promoted);
                        rolledBack++;
                        continue;
                    }

                    if (!assetExists && stagedExists)
                    {
                        File.Delete(journalPath);
                        cleared++;
                        continue;
                    }

                    warnings++;
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or JsonException
                        or FormatException)
                {
                    warnings++;
                }
            }
        }

        return new(completed, rolledBack, cleared, warnings);
    }

    private static async Task<StagedLocalMediaItem> StageOneAsync(
        Guid jobId,
        ValidatedRequest request,
        int itemNumber,
        int itemCount,
        string itemsRoot,
        IProgress<LocalMediaStagingProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stagedDirectory = Path.Combine(itemsRoot, request.TrainingVideoId.ToString("N"));
        Directory.CreateDirectory(stagedDirectory);
        EnsureNotReparsePoint(stagedDirectory);

        var partPath = Path.Combine(stagedDirectory, "original.mp4.part");
        var completedPath = Path.Combine(stagedDirectory, "original.mp4");
        long expectedLength;
        DateTime initialLastWriteUtc;
        long bytesCopied = 0;
        byte[] digest;

        await using (var source = new FileStream(
                         request.SourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         CopyBufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         partPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         CopyBufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            expectedLength = source.Length;
            if (expectedLength == 0)
            {
                throw new IOException("The source MP4 became empty before it could be staged.");
            }

            initialLastWriteUtc = File.GetLastWriteTimeUtc(request.SourcePath);
            var buffer = new byte[CopyBufferSize];

            progress?.Report(new(
                jobId,
                request.TrainingVideoId,
                itemNumber,
                itemCount,
                0,
                expectedLength));
            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesCopied += read;

                progress?.Report(new(
                    jobId,
                    request.TrainingVideoId,
                    itemNumber,
                    itemCount,
                    bytesCopied,
                    expectedLength));
                cancellationToken.ThrowIfCancellationRequested();
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (bytesCopied != expectedLength
                || source.Length != expectedLength
                || File.GetLastWriteTimeUtc(request.SourcePath) != initialLastWriteUtc)
            {
                throw new IOException("The source MP4 changed while it was being staged.");
            }

            digest = hasher.GetHashAndReset();
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(partPath, completedPath);

        return new(
            jobId,
            request.TrainingVideoId,
            Convert.ToHexStringLower(digest),
            bytesCopied,
            request.SourceFileName,
            stagedDirectory,
            completedPath);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    private static PromotionJournalDocument CreatePromotionJournal(
        ProfileWorkspaceLayout layout,
        StagedLocalMediaItem stagedItem,
        Guid assetId,
        string destinationDirectory)
    {
        var stagedRelativePath = NormalizeRelativePath(
            Path.GetRelativePath(layout.WorkspaceRoot, stagedItem.StagedDirectoryPath));
        var assetOriginalRelativePath = NormalizeRelativePath(
            Path.GetRelativePath(
                layout.WorkspaceRoot,
                Path.Combine(destinationDirectory, "original.mp4")));

        return new(
            PromotionJournalVersion,
            stagedItem.JobId,
            stagedItem.TrainingVideoId,
            assetId,
            stagedItem.Sha256,
            stagedItem.ByteLength,
            stagedRelativePath,
            assetOriginalRelativePath);
    }

    private static async Task WritePromotionJournalAsync(
        ProfileWorkspaceLayout layout,
        PromotionJournalDocument journal,
        CancellationToken cancellationToken)
    {
        var stagedDirectory = ResolveStagedDirectoryFromJournal(layout, journal);
        var journalPath = GetPromotionJournalPath(
            layout,
            stagedDirectory,
            journal.JobId,
            journal.AssetId,
            createDirectory: true);
        if (File.Exists(journalPath))
        {
            throw new IOException("A promotion journal already exists for this media asset.");
        }

        var temporaryPath = journalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, journalPath);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                // Preserve the original journal-write failure.
            }

            throw;
        }
    }

    private static async Task<PromotionJournalDocument> ReadPromotionJournalAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(journalPath);
        var info = new FileInfo(journalPath);
        if (info.Length <= 0 || info.Length > MaximumPromotionJournalBytes)
        {
            throw new InvalidDataException("A promotion journal has an invalid size.");
        }

        await using var stream = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var journal = await JsonSerializer.DeserializeAsync<PromotionJournalDocument>(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("A promotion journal is empty.");
        ValidatePromotionJournal(journal);
        return journal;
    }

    private static void ValidatePromotionJournal(PromotionJournalDocument journal)
    {
        if (journal.Version != PromotionJournalVersion
            || journal.JobId == Guid.Empty
            || journal.TrainingVideoId == Guid.Empty
            || journal.AssetId == Guid.Empty
            || !IsLowercaseSha256(journal.Sha256)
            || journal.ByteLength <= 0)
        {
            throw new InvalidDataException("A promotion journal contains invalid integrity metadata.");
        }

        if (!string.Equals(
                journal.StagedDirectoryRelativePath,
                NormalizeRelativePath(journal.StagedDirectoryRelativePath),
                StringComparison.Ordinal)
            || !string.Equals(
                journal.AssetOriginalRelativePath,
                NormalizeRelativePath(journal.AssetOriginalRelativePath),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("A promotion journal contains a non-canonical path.");
        }
    }

    private static string ResolveStagedDirectoryFromJournal(
        ProfileWorkspaceLayout layout,
        PromotionJournalDocument journal)
    {
        var relativePath = journal.StagedDirectoryRelativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        var stagedDirectory = RequireContainedPath(
            layout.ProcessingRoot,
            Path.Combine(layout.WorkspaceRoot, relativePath),
            "A promotion journal's staged path escapes Processing.");
        ValidateStagedDirectoryShape(
            layout,
            stagedDirectory,
            journal.TrainingVideoId,
            journal.JobId);
        return stagedDirectory;
    }

    private static string ValidateStoredAssetLocation(
        ProfileWorkspaceLayout layout,
        string workspaceRelativeOriginalPath,
        Guid assetId)
    {
        if (string.IsNullOrWhiteSpace(workspaceRelativeOriginalPath)
            || Path.IsPathFullyQualified(workspaceRelativeOriginalPath))
        {
            throw new ArgumentException(
                "The persisted media path must be workspace-relative.",
                nameof(workspaceRelativeOriginalPath));
        }

        var normalizedRelativePath = NormalizeRelativePath(workspaceRelativeOriginalPath);
        if (!string.Equals(
                normalizedRelativePath,
                workspaceRelativeOriginalPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The persisted media path is not canonical.");
        }

        var originalPath = RequireContainedPath(
            layout.MediaRoot,
            Path.Combine(
                layout.WorkspaceRoot,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "The persisted media path escapes Media.");
        if (!string.Equals(Path.GetFileName(originalPath), "original.mp4", StringComparison.Ordinal)
            || Path.GetDirectoryName(originalPath) is not { } assetDirectory)
        {
            throw new InvalidDataException("The persisted media path has an invalid asset shape.");
        }

        RequireDirectChild(
            layout.MediaRoot,
            assetDirectory,
            "A persisted media asset must be directly beneath Media.");
        if (!new DirectoryInfo(assetDirectory).Name.EndsWith(
                $"_{ShortId(assetId)}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The persisted media directory does not match its asset ID.");
        }

        return originalPath;
    }

    private static async Task VerifyStagedJournalItemAsync(
        string stagedDirectory,
        PromotionJournalDocument journal,
        CancellationToken cancellationToken)
    {
        EnsureTreeHasNoReparsePoints(stagedDirectory);
        var stagedOriginal = Path.Combine(stagedDirectory, "original.mp4");
        if (!File.Exists(stagedOriginal) || new FileInfo(stagedOriginal).Length != journal.ByteLength)
        {
            throw new InvalidDataException("A journaled staged media file is missing or has changed length.");
        }

        var hash = await ComputeSha256Async(stagedOriginal, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, journal.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A journaled staged media file no longer matches its SHA-256.");
        }
    }

    private static string GetPromotionJournalPath(
        ProfileWorkspaceLayout layout,
        string stagedDirectory,
        Guid jobId,
        Guid assetId,
        bool createDirectory)
    {
        ValidateStagedDirectoryShape(layout, stagedDirectory, trainingVideoId: null, jobId);
        var itemsDirectory = Directory.GetParent(stagedDirectory)
            ?? throw new IOException("The staged media directory has no items parent.");
        var jobDirectory = itemsDirectory.Parent
            ?? throw new IOException("The staged media directory has no job parent.");
        var journalDirectory = RequireContainedPath(
            jobDirectory.FullName,
            Path.Combine(jobDirectory.FullName, ".promotion-journal"),
            "The promotion journal path escapes its job.");
        RequireDirectChild(
            jobDirectory.FullName,
            journalDirectory,
            "A promotion journal must be directly beneath its job.");

        if (createDirectory)
        {
            Directory.CreateDirectory(journalDirectory);
            EnsureNotReparsePoint(journalDirectory);
        }
        else if (Directory.Exists(journalDirectory))
        {
            EnsureNotReparsePoint(journalDirectory);
        }

        return Path.Combine(journalDirectory, assetId.ToString("N") + ".json");
    }

    private static void DeletePromotionJournal(
        ProfileWorkspaceLayout layout,
        string stagedDirectory,
        Guid jobId,
        Guid assetId)
    {
        var journalPath = GetPromotionJournalPath(
            layout,
            stagedDirectory,
            jobId,
            assetId,
            createDirectory: false);
        File.Delete(journalPath);
    }

    private static void ValidatePromotionJournalFileName(string journalPath, Guid assetId)
    {
        if (!string.Equals(
                Path.GetFileName(journalPath),
                assetId.ToString("N") + ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A promotion journal filename does not match its asset ID.");
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A relative path is required.", nameof(path));
        }

        var slashPath = path.Replace('\\', '/');
        var segments = slashPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
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

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<ValidatedRequest> ValidateRequests(
        IReadOnlyCollection<LocalMediaStageRequest> requests)
    {
        var ids = new HashSet<Guid>();
        var validated = new List<ValidatedRequest>(requests.Count);

        foreach (var request in requests)
        {
            if (request is null)
            {
                throw new ArgumentException("A local media request cannot be null.", nameof(requests));
            }

            if (request.TrainingVideoId == Guid.Empty)
            {
                throw new ArgumentException("A training video ID cannot be empty.", nameof(requests));
            }

            if (!ids.Add(request.TrainingVideoId))
            {
                throw new ArgumentException("Training video IDs must be unique within a staging job.", nameof(requests));
            }

            if (string.IsNullOrWhiteSpace(request.SourceFilePath)
                || !Path.IsPathFullyQualified(request.SourceFilePath))
            {
                throw new ArgumentException("Each source MP4 must use an absolute path.", nameof(requests));
            }

            string sourcePath;
            try
            {
                sourcePath = Path.GetFullPath(request.SourceFilePath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArgumentException("A source MP4 path is invalid.", nameof(requests), exception);
            }

            if (!string.Equals(Path.GetExtension(sourcePath), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Only .mp4 source files are supported.", nameof(requests));
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("A selected source MP4 does not exist.", sourcePath);
            }

            EnsureNotReparsePoint(sourcePath);
            if (new FileInfo(sourcePath).Length == 0)
            {
                throw new ArgumentException("A source MP4 cannot be empty.", nameof(requests));
            }

            validated.Add(new(request.TrainingVideoId, sourcePath, Path.GetFileName(sourcePath)));
        }

        return validated;
    }

    private static void ValidateLayout(ProfileWorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var validation = WorkspacePathPolicy.Validate(layout.WorkspaceRoot);
        if (!validation.IsValid || !PathsEqual(validation.NormalizedPath!, layout.WorkspaceRoot))
        {
            throw new ArgumentException("The profile workspace layout is invalid.", nameof(layout));
        }

        if (!Directory.Exists(layout.WorkspaceRoot)
            || !Directory.Exists(layout.ProcessingRoot)
            || !Directory.Exists(layout.MediaRoot))
        {
            throw new DirectoryNotFoundException("Initialize the profile workspace before staging media.");
        }

        var expectedProcessing = Path.Combine(layout.WorkspaceRoot, "Processing");
        var expectedMedia = Path.Combine(layout.WorkspaceRoot, "Media");
        if (!PathsEqual(layout.ProcessingRoot, expectedProcessing)
            || !PathsEqual(layout.MediaRoot, expectedMedia))
        {
            throw new ArgumentException("The profile workspace layout has invalid media boundaries.", nameof(layout));
        }

        EnsurePathSegmentsHaveNoReparsePoints(layout.WorkspaceRoot, layout.ProcessingRoot);
        EnsurePathSegmentsHaveNoReparsePoints(layout.WorkspaceRoot, layout.MediaRoot);
    }

    private static string ValidateStagedItemLocation(
        ProfileWorkspaceLayout layout,
        StagedLocalMediaItem stagedItem)
    {
        if (stagedItem.JobId == Guid.Empty || stagedItem.TrainingVideoId == Guid.Empty)
        {
            throw new ArgumentException("The staged media identity is invalid.", nameof(stagedItem));
        }

        if (stagedItem.ByteLength <= 0 || string.IsNullOrWhiteSpace(stagedItem.Sha256))
        {
            throw new ArgumentException("The staged media integrity metadata is invalid.", nameof(stagedItem));
        }

        var stagedDirectory = RequireContainedPath(
            layout.ProcessingRoot,
            stagedItem.StagedDirectoryPath,
            "The staged directory escapes Processing.");
        ValidateStagedDirectoryShape(layout, stagedDirectory, stagedItem.TrainingVideoId, stagedItem.JobId);

        if (!Directory.Exists(stagedDirectory))
        {
            throw new DirectoryNotFoundException("The staged media directory does not exist.");
        }

        EnsurePathSegmentsHaveNoReparsePoints(layout.ProcessingRoot, stagedDirectory);
        var jobDirectory = Directory.GetParent(Path.GetDirectoryName(stagedDirectory)!)!;
        EnsureTreeHasNoReparsePoints(jobDirectory.FullName);

        return stagedDirectory;
    }

    private static void ValidateStagedDirectoryShape(
        ProfileWorkspaceLayout layout,
        string stagedDirectory,
        Guid? trainingVideoId,
        Guid? jobId)
    {
        var itemDirectory = new DirectoryInfo(stagedDirectory);
        var itemsDirectory = itemDirectory.Parent;
        var jobDirectory = itemsDirectory?.Parent;
        var processingDirectory = jobDirectory?.Parent;

        if ((trainingVideoId is { } requiredTrainingVideoId
                && !string.Equals(
                    itemDirectory.Name,
                    requiredTrainingVideoId.ToString("N"),
                    StringComparison.OrdinalIgnoreCase))
            || !string.Equals(itemsDirectory?.Name, "items", StringComparison.Ordinal)
            || processingDirectory is null
            || !PathsEqual(processingDirectory.FullName, layout.ProcessingRoot))
        {
            throw new IOException("The staged media directory has an invalid job structure.");
        }

        if (jobId is { } requiredJobId
            && !jobDirectory!.Name.EndsWith($"_{ShortId(requiredJobId)}", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The staged media directory does not belong to the stated job.");
        }
    }

    private static string BuildReadableAssetDirectoryName(
        string? recordingLabel,
        string? safeSourceName,
        Guid assetId)
    {
        var label = SanitizeName(recordingLabel, "recording");
        var sourceStem = string.IsNullOrWhiteSpace(safeSourceName)
            ? "media"
            : GetFileNameStem(safeSourceName);
        var source = SanitizeName(sourceStem, "media");
        return $"{label}_{source}_{ShortId(assetId)}";
    }

    private static string GetFileNameStem(string value)
    {
        var finalSeparator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        var fileName = value[(finalSeparator + 1)..];
        var extensionSeparator = fileName.LastIndexOf('.');
        return extensionSeparator > 0 ? fileName[..extensionSeparator] : fileName;
    }

    private static string SanitizeName(string? value, string fallback)
    {
        const int maximumLength = 48;
        var builder = new StringBuilder(maximumLength);
        var separatorPending = false;

        foreach (var character in value?.Trim() ?? string.Empty)
        {
            if (builder.Length >= maximumLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character))
            {
                if (separatorPending && builder.Length > 0 && builder.Length < maximumLength)
                {
                    builder.Append('-');
                }

                builder.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = builder.Length > 0;
            }
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    private static void EnsureTreeHasNoReparsePoints(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Required directory not found: {root}");
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
                    throw new IOException("Reparse points are not allowed inside profile media boundaries.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void EnsurePathSegmentsHaveNoReparsePoints(string root, string target)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedTarget = RequireContainedPath(
            normalizedRoot,
            target,
            "The path escapes its workspace boundary.");

        EnsureNotReparsePoint(normalizedRoot);
        if (PathsEqual(normalizedRoot, normalizedTarget))
        {
            return;
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        var current = normalizedRoot;
        foreach (var segment in relative.Split(
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

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Reparse points are not allowed inside profile media boundaries.");
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
            throw new ArgumentException(error, nameof(candidate), exception);
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        if (Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
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

    private static string ShortId(Guid id) => id.ToString("N")[..ShortIdLength];

    private sealed record ValidatedRequest(Guid TrainingVideoId, string SourcePath, string SourceFileName);

    private sealed record PromotionJournalDocument(
        int Version,
        Guid JobId,
        Guid TrainingVideoId,
        Guid AssetId,
        string Sha256,
        long ByteLength,
        string StagedDirectoryRelativePath,
        string AssetOriginalRelativePath);
}
