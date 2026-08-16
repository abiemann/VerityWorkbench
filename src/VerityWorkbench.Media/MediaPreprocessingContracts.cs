namespace VerityWorkbench.Media;

/// <summary>
/// Identifies one already validated immutable media asset to preprocess.
/// The absolute source path is transient and is never written to an artifact.
/// </summary>
public sealed record MediaPreprocessingRequest(
    Guid JobId,
    Guid MediaAssetId,
    string MediaFilePath,
    string ExpectedSourceSha256,
    long ExpectedSourceByteLength,
    ValidatedMediaMetadata Validation);

public enum MediaPreprocessingPhase
{
    ProbingTimeline,
    GeneratingArtifacts,
    VerifyingArtifacts,
    HashingArtifacts,
    WritingManifests,
    Completed,
}

public sealed record MediaPreprocessingProgress(
    Guid MediaAssetId,
    MediaPreprocessingPhase Phase);

/// <summary>
/// Normalized successful output. Its property names intentionally map one-to-one
/// to the persistence record without taking a dependency on VerityWorkbench.Data.
/// Workspace-relative paths use forward slashes and contain no source path.
/// </summary>
public sealed record MediaPreprocessingResult(
    Guid MediaAssetId,
    string SourceSha256,
    long SourceByteLength,
    string PreprocessingContractVersion,
    string PreprocessingContractSha256,
    string ProxyWorkspaceRelativePath,
    string ProxySha256,
    long ProxyByteLength,
    string ProxyContainerFormat,
    string ProxyVideoCodec,
    string ProxyPixelFormat,
    int ProxyWidth,
    int ProxyHeight,
    long ProxyFrameRateNumerator,
    long ProxyFrameRateDenominator,
    string ProxyAudioCodec,
    int ProxyAudioSampleRateHz,
    int ProxyAudioChannelCount,
    long ProxyDurationMicroseconds,
    string AnalysisAudioWorkspaceRelativePath,
    string AnalysisAudioSha256,
    long AnalysisAudioByteLength,
    string AnalysisAudioCodec,
    int AnalysisAudioSampleRateHz,
    int AnalysisAudioChannelCount,
    long AnalysisAudioSampleCount,
    long AnalysisAudioDurationMicroseconds,
    string TimestampMapWorkspaceRelativePath,
    string TimestampMapSha256,
    long TimestampMapByteLength,
    string ManifestWorkspaceRelativePath,
    string ManifestSha256,
    long ManifestByteLength,
    long SourceTimelineOriginMicroseconds,
    long MappedDurationMicroseconds,
    int VideoMapEntryCount,
    int AudioMapSegmentCount,
    string FfmpegVersion,
    string FfmpegCompilerIdentifier,
    string FfmpegConfigurationSha256,
    string FfmpegExecutableSha256,
    string MediaValidationContractSha256,
    string MediaQualityState,
    string ModelApplicabilityState,
    DateTimeOffset PreprocessedAtUtc);

public sealed record StagedMediaPreprocessingResult(
    Guid JobId,
    string StagedOutputDirectoryPath,
    string IntendedPreparedDirectoryPath,
    MediaPreprocessingResult Output);

public sealed record PromotedMediaPreprocessingResult(
    Guid JobId,
    string PreparedDirectoryPath,
    string OriginatingStagedDirectoryPath,
    MediaPreprocessingResult Output);

public sealed record MediaPreprocessingPromotionReconciliationResult(
    int CompletedCount,
    int RolledBackCount,
    int ClearedCount,
    int WarningCount,
    IReadOnlyList<Guid> IntegrityFailedAssetIds);

public enum MediaPreparedVerificationState
{
    Verified,
    IntegrityMismatch,
    OperationalFailure,
}

public sealed record MediaPreparedVerificationResult(
    MediaPreparedVerificationState State,
    string? FailureReason)
{
    public bool IsValid => State == MediaPreparedVerificationState.Verified;
}

/// <summary>
/// Owns the exact read-only proxy handle whose bytes were verified. The handle
/// denies write and delete sharing until the consumer disposes the lease.
/// </summary>
public sealed class PreparedMediaProxyLease : IDisposable, IAsyncDisposable
{
    private FileStream? _stream;

    internal PreparedMediaProxyLease(FileStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public Stream Stream => _stream
        ?? throw new ObjectDisposedException(nameof(PreparedMediaProxyLease));

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();

    public async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed record PreparedMediaProxyOpenResult(
    MediaPreparedVerificationState State,
    PreparedMediaProxyLease? Lease,
    string? FailureReason)
{
    public bool IsOpen => State == MediaPreparedVerificationState.Verified && Lease is not null;
}

public enum MediaPreprocessingFailure
{
    ToolContractInvalid,
    ToolIntegrityMismatch,
    PreflightMismatch,
    WorkspaceInvalid,
    ProcessingPathInvalid,
    MediaPathInvalid,
    SourceIntegrityInvalid,
    SourceIntegrityChanged,
    TimelineProbeLaunchFailed,
    TimelineProbeTimedOut,
    TimelineProbeOutputLimitExceeded,
    TimelineProbeFailed,
    TimelineProbeMalformed,
    GenerationLaunchFailed,
    GenerationTimedOut,
    GenerationOutputLimitExceeded,
    GenerationFailed,
    GenerationProgressMalformed,
    ArtifactProbeLaunchFailed,
    ArtifactProbeTimedOut,
    ArtifactProbeOutputLimitExceeded,
    ArtifactProbeFailed,
    ArtifactProbeMalformed,
    ArtifactContractMismatch,
    ArtifactIntegrityInvalid,
    ManifestWriteFailed,
}

public sealed class MediaPreprocessingException : Exception
{
    public MediaPreprocessingException(MediaPreprocessingFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public MediaPreprocessingFailure Failure { get; }
}
