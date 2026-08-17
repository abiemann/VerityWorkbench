namespace VerityWorkbench.Media;

/// <summary>
/// Exact, whole-file integer observations over one verified analysis WAV.
/// This contract contains no label, path, clock, threshold, or interpretation.
/// </summary>
public sealed record AudioPcmObservationResult(
    Guid MediaAssetId,
    string ObservationContractVersion,
    string ObservationContractSha256,
    string SourceSha256,
    long SourceByteLength,
    string PreprocessingContractVersion,
    string PreprocessingContractSha256,
    string AnalysisAudioSha256,
    long AnalysisAudioByteLength,
    int WaveFormatTag,
    string SampleEncoding,
    int SampleRateHz,
    int ChannelCount,
    int BitsPerSample,
    int BlockAlignBytes,
    int ByteRate,
    long CommittedSampleCount,
    long ProcessedSampleCount,
    long DurationMicroseconds,
    int MinimumSample,
    int MaximumSample,
    int AbsolutePeakSample,
    long PositiveSampleCount,
    long NegativeSampleCount,
    long ZeroSampleCount,
    long PositiveFullScaleSampleCount,
    long NegativeFullScaleSampleCount,
    long AdjacentOppositeSignCrossingCount,
    string SampleSum,
    string SquaredSampleSum);

public enum AudioPcmObservationFailure
{
    WorkspaceInvalid,
    PreparedIntegrityMismatch,
    PreparedOperationalFailure,
    PreparedMetadataMismatch,
    WaveMalformed,
    WaveContractMismatch,
}

public sealed class AudioPcmObservationException : Exception
{
    public AudioPcmObservationException(AudioPcmObservationFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public AudioPcmObservationFailure Failure { get; }
}
