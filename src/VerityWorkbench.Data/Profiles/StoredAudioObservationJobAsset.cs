namespace VerityWorkbench.Data.Profiles;

public sealed record StoredAudioObservationJobAsset(
    Guid JobId,
    Guid MediaAssetId,
    string AnalysisAudioWorkspaceRelativePath,
    string AnalysisAudioSha256,
    long AnalysisAudioByteLength,
    int AnalysisAudioSampleRateHz,
    int AnalysisAudioChannelCount,
    long AnalysisAudioSampleCount,
    long AnalysisAudioDurationMicroseconds,
    string PreprocessingContractSha256,
    string ObservationContractVersion,
    string ObservationContractSha256);
