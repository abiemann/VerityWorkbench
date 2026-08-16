namespace VerityWorkbench.Media;

/// <summary>
/// Pins one external executable and bounds every invocation of it.
/// Executable paths are transient inputs and are never returned as provenance.
/// </summary>
public sealed record MediaValidationExecutableContract(
    string ExecutablePath,
    string ExpectedSha256,
    TimeSpan PreflightTimeout,
    TimeSpan InvocationTimeout,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes);

/// <summary>
/// Pins the matching ffprobe and ffmpeg binaries used for one validation.
/// </summary>
public sealed record MediaValidationToolContract(
    MediaValidationExecutableContract Ffprobe,
    MediaValidationExecutableContract Ffmpeg,
    string ExpectedVersionPrefix,
    string ValidationContractVersion);

public sealed record MediaValidationToolProvenance(
    string Version,
    string CompilerIdentifier,
    string Configuration,
    string ConfigurationSha256,
    string ExecutableSha256);

/// <summary>
/// Proof that both pinned tools were hashed and matched before media probing.
/// It intentionally contains no executable or working-directory paths.
/// </summary>
public sealed class MediaValidationPreflight
{
    internal MediaValidationPreflight(
        MediaValidationToolProvenance ffprobe,
        MediaValidationToolProvenance ffmpeg,
        string validationContractSha256)
    {
        Ffprobe = ffprobe;
        Ffmpeg = ffmpeg;
        ValidationContractSha256 = validationContractSha256;
    }

    public MediaValidationToolProvenance Ffprobe { get; }

    public MediaValidationToolProvenance Ffmpeg { get; }

    public string ValidationContractSha256 { get; }
}

public sealed record ValidatedVideoStreamMetadata(
    int StreamIndex,
    string CodecName,
    int Width,
    int Height,
    long FrameRateNumerator,
    long FrameRateDenominator);

public sealed record ValidatedAudioStreamMetadata(
    int StreamIndex,
    string CodecName,
    int SampleRateHz,
    int ChannelCount);

/// <summary>
/// Normalized metadata produced only after probe validation and a complete
/// software decode of the selected video and audio streams.
/// </summary>
public sealed record ValidatedMediaMetadata(
    string ContainerFormat,
    string? ContainerMajorBrand,
    long DurationMicroseconds,
    ValidatedVideoStreamMetadata Video,
    ValidatedAudioStreamMetadata Audio,
    MediaValidationToolProvenance Ffprobe,
    MediaValidationToolProvenance Ffmpeg,
    string ValidationContractSha256,
    long DecodedDurationMicroseconds)
{
    public bool DecodeCompleted => true;
}

public enum MediaValidationFailure
{
    ToolContractInvalid,
    ToolUnavailable,
    ToolIntegrityMismatch,
    ToolIdentityMalformed,
    ToolIdentityMismatch,
    ToolLaunchFailed,
    ToolIdentityTimedOut,
    ToolIdentityOutputLimitExceeded,
    WorkingDirectoryInvalid,
    MediaPathInvalid,
    MediaIntegrityMetadataInvalid,
    IntegrityChanged,
    ProbeLaunchFailed,
    ProbeTimedOut,
    ProbeOutputLimitExceeded,
    ProbeRejectedMedia,
    ProbeOutputMalformed,
    UnsupportedContainer,
    InvalidDuration,
    MissingVideoStream,
    InvalidVideoStream,
    AmbiguousVideoStreams,
    MissingAudioStream,
    InvalidAudioStream,
    AmbiguousAudioStreams,
    DecodeLaunchFailed,
    DecodeTimedOut,
    DecodeOutputLimitExceeded,
    UnsupportedCodec,
    CorruptMedia,
    DecodeProgressMalformed,
}

public sealed class MediaValidationException : Exception
{
    public MediaValidationException(MediaValidationFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public MediaValidationFailure Failure { get; }
}
