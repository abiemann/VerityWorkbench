using System.Text.Json;
using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media;

public sealed partial class MediaPreprocessingService
{
    public async Task<PromotedMediaPreprocessingResult> PromoteAsync(
        ProfileWorkspaceLayout layout,
        StagedMediaPreprocessingResult staged,
        CancellationToken cancellationToken = default)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(staged);
        cancellationToken.ThrowIfCancellationRequested();

        var stagedDirectory = ValidateStagedDirectory(layout, staged);
        await VerifyBundleDirectoryAsync(stagedDirectory, staged.Output, cancellationToken)
            .ConfigureAwait(false);

        var preparedDirectory = ValidatePreparedDirectoryPath(
            layout,
            staged.Output,
            requireExists: false);
        if (!PathsEqual(preparedDirectory, staged.IntendedPreparedDirectoryPath))
        {
            throw new InvalidDataException("The staged result has an inconsistent prepared-media destination.");
        }

        if (Directory.Exists(preparedDirectory) || File.Exists(preparedDirectory))
        {
            throw new IOException("The immutable prepared-media destination already exists.");
        }

        var journal = new PromotionJournalDocument(
            JournalVersion,
            staged.JobId,
            staged.Output.MediaAssetId,
            NormalizeRelativePath(Path.GetRelativePath(layout.WorkspaceRoot, stagedDirectory)),
            NormalizeRelativePath(Path.GetRelativePath(layout.WorkspaceRoot, preparedDirectory)),
            staged.Output);
        await WritePromotionJournalAsync(layout, journal, cancellationToken).ConfigureAwait(false);

        // Once the durable journal exists, the directory move is deliberately a
        // short non-cancellable commit section. A crash is resolved from journal.
        var preparedRoot = Directory.GetParent(preparedDirectory)
            ?? throw new IOException("The prepared-media destination has no parent.");
        Directory.CreateDirectory(preparedRoot.FullName);
        EnsurePathSegmentsHaveNoReparsePoints(layout.MediaRoot, preparedRoot.FullName);
        Directory.Move(stagedDirectory, preparedDirectory);

        return new(
            staged.JobId,
            preparedDirectory,
            stagedDirectory,
            staged.Output);
    }

    /// <summary>
    /// Clears the crash journal only after the corresponding database transaction
    /// commits. No media bytes are changed.
    /// </summary>
    public void ConfirmPromotion(
        ProfileWorkspaceLayout layout,
        PromotedMediaPreprocessingResult promoted)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(promoted);
        var preparedDirectory = ValidatePreparedDirectoryPath(layout, promoted.Output, requireExists: true);
        if (!PathsEqual(preparedDirectory, promoted.PreparedDirectoryPath))
        {
            throw new InvalidDataException("The promoted prepared-media path is inconsistent.");
        }

        DeletePromotionJournal(
            layout,
            promoted.OriginatingStagedDirectoryPath,
            promoted.JobId,
            promoted.Output.MediaAssetId);
    }

    /// <summary>
    /// Moves an uncommitted promoted bundle back into Processing. No bytes are
    /// deleted or overwritten.
    /// </summary>
    public void RollbackPromotion(
        ProfileWorkspaceLayout layout,
        PromotedMediaPreprocessingResult promoted)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(promoted);
        var preparedDirectory = ValidatePreparedDirectoryPath(layout, promoted.Output, requireExists: true);
        if (!PathsEqual(preparedDirectory, promoted.PreparedDirectoryPath))
        {
            throw new InvalidDataException("The promoted prepared-media path is inconsistent.");
        }

        EnsureTreeHasNoReparsePoints(preparedDirectory);
        var stagedDirectory = RequireContainedPath(
            layout.ProcessingRoot,
            promoted.OriginatingStagedDirectoryPath,
            "The rollback destination escapes Processing.");
        ValidateStagedDirectoryShape(layout, stagedDirectory, promoted.JobId, promoted.Output.MediaAssetId);
        if (Directory.Exists(stagedDirectory) || File.Exists(stagedDirectory))
        {
            throw new IOException("The rollback destination already exists.");
        }

        Directory.Move(preparedDirectory, stagedDirectory);
        DeletePromotionJournal(
            layout,
            stagedDirectory,
            promoted.JobId,
            promoted.Output.MediaAssetId);
    }

    /// <summary>
    /// Verifies a committed bundle without changing files. Failure information is
    /// bounded and contains no workspace path.
    /// </summary>
    public async Task<MediaPreparedVerificationResult> VerifyPreparedAsync(
        ProfileWorkspaceLayout layout,
        MediaPreprocessingResult committed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateLayout(layout);
            ArgumentNullException.ThrowIfNull(committed);
            var preparedDirectory = ValidatePreparedDirectoryPath(layout, committed, requireExists: true);
            await VerifyBundleDirectoryAsync(preparedDirectory, committed, cancellationToken)
                .ConfigureAwait(false);
            return new(MediaPreparedVerificationState.Verified, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is MediaIntegrityException
                or ArgumentException
                or InvalidDataException
                or FileNotFoundException
                or DirectoryNotFoundException
                or InvalidOperationException)
        {
            return new(
                MediaPreparedVerificationState.IntegrityMismatch,
                "The committed prepared-media bundle is missing, unsafe, or no longer matches its recorded integrity metadata.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new(
                MediaPreparedVerificationState.OperationalFailure,
                "The committed prepared-media bundle could not be read. Its integrity state was not changed.");
        }
    }

    /// <summary>
    /// Verifies the complete committed bundle and returns the exact proxy handle
    /// whose bytes were checked. The open handle denies write and delete sharing,
    /// so the consumer cannot be redirected to replacement bytes after verification.
    /// </summary>
    public async Task<PreparedMediaProxyOpenResult> OpenVerifiedProxyAsync(
        ProfileWorkspaceLayout layout,
        MediaPreprocessingResult committed,
        CancellationToken cancellationToken = default)
    {
        FileStream? proxyStream = null;
        try
        {
            ValidateLayout(layout);
            ArgumentNullException.ThrowIfNull(committed);
            var preparedDirectory = ValidatePreparedDirectoryPath(
                layout,
                committed,
                requireExists: true);
            var proxyPath = ResolveRelativePath(
                layout,
                committed.ProxyWorkspaceRelativePath,
                "The prepared proxy path escapes Media.");
            var proxyAttributes = File.GetAttributes(proxyPath);
            if ((proxyAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "The prepared proxy must be a regular file inside the prepared-media bundle.");
            }

            proxyStream = new FileStream(
                proxyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            await VerifyOpenProxyStreamAsync(
                    proxyStream,
                    committed.ProxySha256,
                    committed.ProxyByteLength,
                    cancellationToken)
                .ConfigureAwait(false);

            // Verify the complete bundle while the exact proxy is locked against
            // write/delete replacement. This also repeats structural/reparse checks
            // after the handle is open.
            await VerifyBundleDirectoryAsync(preparedDirectory, committed, cancellationToken)
                .ConfigureAwait(false);
            proxyStream.Position = 0;
            var lease = new PreparedMediaProxyLease(proxyStream);
            proxyStream = null;
            return new(MediaPreparedVerificationState.Verified, lease, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is MediaIntegrityException
                or ArgumentException
                or InvalidDataException
                or FileNotFoundException
                or DirectoryNotFoundException
                or InvalidOperationException)
        {
            return new(
                MediaPreparedVerificationState.IntegrityMismatch,
                Lease: null,
                "The committed prepared-media bundle is missing, unsafe, or no longer matches its recorded integrity metadata.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new(
                MediaPreparedVerificationState.OperationalFailure,
                Lease: null,
                "The committed prepared-media bundle could not be read. Its integrity state was not changed.");
        }
        finally
        {
            if (proxyStream is not null)
            {
                await proxyStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reconciles only journals whose jobs are known to be terminal or stale.
    /// Fresh jobs from another app process must not be included in eligibleJobIds.
    /// </summary>
    public async Task<MediaPreprocessingPromotionReconciliationResult> ReconcilePendingPromotionsAsync(
        ProfileWorkspaceLayout layout,
        IReadOnlyDictionary<Guid, string> committedManifestPaths,
        IReadOnlySet<Guid> eligibleJobIds,
        CancellationToken cancellationToken = default)
    {
        ValidateLayout(layout);
        ArgumentNullException.ThrowIfNull(committedManifestPaths);
        ArgumentNullException.ThrowIfNull(eligibleJobIds);

        var completed = 0;
        var rolledBack = 0;
        var cleared = 0;
        var warnings = 0;
        var integrityFailures = new HashSet<Guid>();

        foreach (var jobDirectory in Directory.EnumerateDirectories(layout.ProcessingRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string journalDirectory;
            try
            {
                RequireDirectChild(layout.ProcessingRoot, jobDirectory, "A processing job has an invalid location.");
                EnsureNotReparsePoint(jobDirectory);
                journalDirectory = Path.Combine(jobDirectory, ".preprocessing-promotion-journal");
                if (!Directory.Exists(journalDirectory))
                {
                    continue;
                }

                EnsureNotReparsePoint(journalDirectory);
                RequireDirectChild(jobDirectory, journalDirectory, "A preprocessing journal has an invalid location.");
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
                PromotionJournalDocument? journal = null;
                try
                {
                    journal = await ReadPromotionJournalAsync(journalPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (!eligibleJobIds.Contains(journal.JobId))
                    {
                        continue;
                    }

                    ValidateJournalFileName(journalPath, journal.MediaAssetId);
                    var stagedDirectory = ResolveStagedDirectory(layout, journal);
                    var preparedDirectory = ResolvePreparedDirectory(layout, journal);
                    var stagedExists = DirectoryExistsOrThrows(stagedDirectory);
                    var preparedExists = DirectoryExistsOrThrows(preparedDirectory);
                    var isCommitted = committedManifestPaths.TryGetValue(
                        journal.MediaAssetId,
                        out var committedManifestPath);

                    if (isCommitted)
                    {
                        if (!string.Equals(
                                NormalizeRelativePath(committedManifestPath!),
                                journal.Output.ManifestWorkspaceRelativePath,
                                StringComparison.Ordinal))
                        {
                            integrityFailures.Add(journal.MediaAssetId);
                            warnings++;
                            continue;
                        }

                        if (preparedExists && !stagedExists)
                        {
                            await VerifyBundleDirectoryAsync(
                                    preparedDirectory,
                                    journal.Output,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            File.Delete(journalPath);
                            completed++;
                            continue;
                        }

                        if (!preparedExists && stagedExists)
                        {
                            await VerifyBundleDirectoryAsync(stagedDirectory, journal.Output, cancellationToken)
                                .ConfigureAwait(false);
                            var preparedRoot = Directory.GetParent(preparedDirectory)
                                ?? throw new IOException("The prepared-media path has no parent.");
                            Directory.CreateDirectory(preparedRoot.FullName);
                            EnsurePathSegmentsHaveNoReparsePoints(layout.MediaRoot, preparedRoot.FullName);
                            Directory.Move(stagedDirectory, preparedDirectory);
                            await VerifyBundleDirectoryAsync(
                                    preparedDirectory,
                                    journal.Output,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            File.Delete(journalPath);
                            completed++;
                            continue;
                        }

                        integrityFailures.Add(journal.MediaAssetId);
                        warnings++;
                        continue;
                    }

                    if (preparedExists && !stagedExists)
                    {
                        await VerifyBundleDirectoryAsync(preparedDirectory, journal.Output, cancellationToken)
                            .ConfigureAwait(false);
                        var stagedParent = Directory.GetParent(stagedDirectory)
                            ?? throw new IOException("The staged preprocessing path has no parent.");
                        Directory.CreateDirectory(stagedParent.FullName);
                        EnsurePathSegmentsHaveNoReparsePoints(layout.ProcessingRoot, stagedParent.FullName);
                        Directory.Move(preparedDirectory, stagedDirectory);
                        File.Delete(journalPath);
                        rolledBack++;
                        continue;
                    }

                    if (!preparedExists && stagedExists)
                    {
                        await VerifyBundleDirectoryAsync(stagedDirectory, journal.Output, cancellationToken)
                            .ConfigureAwait(false);
                        File.Delete(journalPath);
                        cleared++;
                        continue;
                    }

                    warnings++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or IOException
                        or InvalidDataException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or JsonException
                        or FormatException)
                {
                    if (journal is not null
                        && committedManifestPaths.ContainsKey(journal.MediaAssetId)
                        && exception is (ArgumentException
                            or InvalidDataException
                            or FileNotFoundException
                            or DirectoryNotFoundException
                            or InvalidOperationException
                            or JsonException
                            or FormatException))
                    {
                        integrityFailures.Add(journal.MediaAssetId);
                    }

                    warnings++;
                }
            }
        }

        return new(
            completed,
            rolledBack,
            cleared,
            warnings,
            integrityFailures.Order().ToArray());
    }

    private static async Task VerifyBundleDirectoryAsync(
        string directory,
        MediaPreprocessingResult output,
        CancellationToken cancellationToken)
    {
        EnsureTreeHasNoReparsePoints(directory);
        var expected = new[]
        {
            new ExpectedArtifact("proxy.mp4", output.ProxySha256, output.ProxyByteLength),
            new ExpectedArtifact("audio.wav", output.AnalysisAudioSha256, output.AnalysisAudioByteLength),
            new ExpectedArtifact("timestamp-map.json", output.TimestampMapSha256, output.TimestampMapByteLength),
            new ExpectedArtifact("preprocessing-manifest.json", output.ManifestSha256, output.ManifestByteLength),
        };

        var actualFiles = Directory.EnumerateFiles(directory).ToArray();
        if (actualFiles.Length != expected.Length
            || Directory.EnumerateDirectories(directory).Any()
            || actualFiles.Any(path => path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("A prepared-media bundle has an invalid file set.");
        }

        foreach (var artifact in expected)
        {
            if (!IsLowercaseSha256(artifact.Sha256) || artifact.ByteLength <= 0)
            {
                throw new InvalidDataException("A prepared-media record has invalid integrity metadata.");
            }

            var path = Path.Combine(directory, artifact.FileName);
            if (!FileExistsOrThrows(path) || new FileInfo(path).Length != artifact.ByteLength)
            {
                throw new InvalidDataException("A prepared-media artifact is missing or changed length.");
            }

            var current = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, artifact.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A prepared-media artifact changed hash.");
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(
            stream,
            cancellationToken));
    }

    private static async Task VerifyOpenProxyStreamAsync(
        FileStream stream,
        string expectedSha256,
        long expectedByteLength,
        CancellationToken cancellationToken)
    {
        if (!IsLowercaseSha256(expectedSha256) || expectedByteLength <= 0)
        {
            throw new InvalidDataException(
                "The prepared proxy has invalid integrity metadata.");
        }

        if (!stream.CanRead || !stream.CanSeek || stream.Length != expectedByteLength)
        {
            throw new InvalidDataException(
                "The prepared proxy is missing or changed length.");
        }

        stream.Position = 0;
        var currentSha256 = Convert.ToHexStringLower(
            await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false));
        if (!string.Equals(currentSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The prepared proxy changed hash.");
        }

        stream.Position = 0;
    }

    private static string ValidateStagedDirectory(
        ProfileWorkspaceLayout layout,
        StagedMediaPreprocessingResult staged)
    {
        if (staged.JobId == Guid.Empty || staged.Output.MediaAssetId == Guid.Empty)
        {
            throw new ArgumentException("A staged preprocessing identity is invalid.", nameof(staged));
        }

        var directory = RequireContainedPath(
            layout.ProcessingRoot,
            staged.StagedOutputDirectoryPath,
            "The staged preprocessing path escapes Processing.");
        ValidateStagedDirectoryShape(layout, directory, staged.JobId, staged.Output.MediaAssetId);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The staged preprocessing bundle is missing.");
        }

        EnsurePathSegmentsHaveNoReparsePoints(layout.ProcessingRoot, directory);
        return directory;
    }

    private static void ValidateStagedDirectoryShape(
        ProfileWorkspaceLayout layout,
        string stagedDirectory,
        Guid jobId,
        Guid assetId)
    {
        var item = new DirectoryInfo(stagedDirectory);
        var output = item.Parent;
        var job = output?.Parent;
        var processing = job?.Parent;
        if (!string.Equals(item.Name, assetId.ToString("N"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(output?.Name, "Output", StringComparison.Ordinal)
            || processing is null
            || !PathsEqual(processing.FullName, layout.ProcessingRoot)
            || jobId == Guid.Empty)
        {
            throw new InvalidDataException("A staged preprocessing directory has an invalid shape.");
        }
    }

    private static string ValidatePreparedDirectoryPath(
        ProfileWorkspaceLayout layout,
        MediaPreprocessingResult output,
        bool requireExists)
    {
        ValidateOutputRecord(output);
        var manifestPath = ResolveRelativePath(
            layout,
            output.ManifestWorkspaceRelativePath,
            "The prepared manifest path escapes Media.");
        var preparedDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("The prepared manifest has no directory.");
        var preparedRoot = Directory.GetParent(preparedDirectory);
        var assetDirectory = preparedRoot?.Parent;
        if (!string.Equals(Path.GetFileName(manifestPath), "preprocessing-manifest.json", StringComparison.Ordinal)
            || !string.Equals(preparedRoot?.Name, "Prepared", StringComparison.Ordinal)
            || assetDirectory?.Parent is null
            || !PathsEqual(assetDirectory.Parent.FullName, layout.MediaRoot)
            || !assetDirectory.Name.EndsWith(
                "_" + output.MediaAssetId.ToString("N")[..12],
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                new DirectoryInfo(preparedDirectory).Name,
                "v1_" + output.PreprocessingContractSha256[..12],
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The prepared-media directory has an invalid shape.");
        }

        foreach (var (relativePath, leaf) in new[]
                 {
                     (output.ProxyWorkspaceRelativePath, "proxy.mp4"),
                     (output.AnalysisAudioWorkspaceRelativePath, "audio.wav"),
                     (output.TimestampMapWorkspaceRelativePath, "timestamp-map.json"),
                     (output.ManifestWorkspaceRelativePath, "preprocessing-manifest.json"),
                 })
        {
            var path = ResolveRelativePath(layout, relativePath, "A prepared artifact path escapes Media.");
            if (!PathsEqual(Path.GetDirectoryName(path)!, preparedDirectory)
                || !string.Equals(Path.GetFileName(path), leaf, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A prepared artifact path has an invalid shape.");
            }
        }

        if (requireExists && !DirectoryExistsOrThrows(preparedDirectory))
        {
            throw new DirectoryNotFoundException("The committed prepared-media bundle is missing.");
        }

        if (Directory.Exists(preparedDirectory))
        {
            EnsurePathSegmentsHaveNoReparsePoints(layout.MediaRoot, preparedDirectory);
        }

        return preparedDirectory;
    }

    private static void ValidateOutputRecord(MediaPreprocessingResult output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.MediaAssetId == Guid.Empty
            || !IsLowercaseSha256(output.SourceSha256)
            || output.SourceByteLength <= 0
            || !string.Equals(output.PreprocessingContractVersion, CurrentPreprocessingContractVersion, StringComparison.Ordinal)
            || !IsLowercaseSha256(output.PreprocessingContractSha256)
            || !IsLowercaseSha256(output.ProxySha256)
            || !IsLowercaseSha256(output.AnalysisAudioSha256)
            || !IsLowercaseSha256(output.TimestampMapSha256)
            || !IsLowercaseSha256(output.ManifestSha256)
            || output.ProxyByteLength <= 0
            || output.AnalysisAudioByteLength <= 0
            || output.TimestampMapByteLength <= 0
            || output.ManifestByteLength <= 0
            || !string.Equals(output.MediaQualityState, NotAssessed, StringComparison.Ordinal)
            || !string.Equals(output.ModelApplicabilityState, NotAssessed, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The prepared-media result is invalid.");
        }
    }

    private static string ResolveRelativePath(
        ProfileWorkspaceLayout layout,
        string relativePath,
        string error)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (!string.Equals(normalized, relativePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A prepared-media path is not canonical.");
        }

        return RequireContainedPath(
            layout.MediaRoot,
            Path.Combine(layout.WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            error);
    }

    private static async Task WritePromotionJournalAsync(
        ProfileWorkspaceLayout layout,
        PromotionJournalDocument journal,
        CancellationToken cancellationToken)
    {
        var stagedDirectory = ResolveStagedDirectory(layout, journal);
        var journalPath = GetJournalPath(
            layout,
            stagedDirectory,
            journal.JobId,
            journal.MediaAssetId,
            createDirectory: true);
        if (File.Exists(journalPath))
        {
            throw new IOException("A preprocessing promotion journal already exists.");
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
        if (info.Length <= 0 || info.Length > MaximumJournalBytes)
        {
            throw new InvalidDataException("A preprocessing promotion journal has an invalid size.");
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
            ?? throw new InvalidDataException("A preprocessing promotion journal is empty.");
        ValidateJournal(journal);
        return journal;
    }

    private static void ValidateJournal(PromotionJournalDocument journal)
    {
        if (journal.Version != JournalVersion
            || journal.JobId == Guid.Empty
            || journal.MediaAssetId == Guid.Empty
            || journal.Output.MediaAssetId != journal.MediaAssetId
            || !string.Equals(
                journal.StagedDirectoryRelativePath,
                NormalizeRelativePath(journal.StagedDirectoryRelativePath),
                StringComparison.Ordinal)
            || !string.Equals(
                journal.PreparedDirectoryRelativePath,
                NormalizeRelativePath(journal.PreparedDirectoryRelativePath),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("A preprocessing promotion journal is invalid.");
        }

        ValidateOutputRecord(journal.Output);
    }

    private static string ResolveStagedDirectory(
        ProfileWorkspaceLayout layout,
        PromotionJournalDocument journal)
    {
        var directory = RequireContainedPath(
            layout.ProcessingRoot,
            Path.Combine(
                layout.WorkspaceRoot,
                journal.StagedDirectoryRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "A journaled staged path escapes Processing.");
        ValidateStagedDirectoryShape(layout, directory, journal.JobId, journal.MediaAssetId);
        return directory;
    }

    private static string ResolvePreparedDirectory(
        ProfileWorkspaceLayout layout,
        PromotionJournalDocument journal)
    {
        var directory = ValidatePreparedDirectoryPath(layout, journal.Output, requireExists: false);
        var expectedRelative = NormalizeRelativePath(Path.GetRelativePath(layout.WorkspaceRoot, directory));
        if (!string.Equals(expectedRelative, journal.PreparedDirectoryRelativePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A journaled prepared path is inconsistent.");
        }

        return directory;
    }

    private static string GetJournalPath(
        ProfileWorkspaceLayout layout,
        string stagedDirectory,
        Guid jobId,
        Guid assetId,
        bool createDirectory)
    {
        ValidateStagedDirectoryShape(layout, stagedDirectory, jobId, assetId);
        var output = Directory.GetParent(stagedDirectory)
            ?? throw new IOException("The staged preprocessing path has no Output parent.");
        var job = output.Parent
            ?? throw new IOException("The staged preprocessing path has no job parent.");
        var journalDirectory = Path.Combine(job.FullName, ".preprocessing-promotion-journal");
        RequireDirectChild(job.FullName, journalDirectory, "A preprocessing journal has an invalid path.");
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
        File.Delete(GetJournalPath(layout, stagedDirectory, jobId, assetId, createDirectory: false));
    }

    private static void ValidateJournalFileName(string journalPath, Guid assetId)
    {
        if (!string.Equals(
                Path.GetFileName(journalPath),
                assetId.ToString("N") + ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A preprocessing journal filename does not match its asset ID.");
        }
    }

    private sealed record ExpectedArtifact(string FileName, string Sha256, long ByteLength);
    private sealed record PromotionJournalDocument(
        int Version,
        Guid JobId,
        Guid MediaAssetId,
        string StagedDirectoryRelativePath,
        string PreparedDirectoryRelativePath,
        MediaPreprocessingResult Output);
}
