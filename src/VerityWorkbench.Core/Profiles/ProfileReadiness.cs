namespace VerityWorkbench.Core.Profiles;

public enum ProfileReadiness
{
    Draft,
    IngestingMedia,
    MediaIngestedAwaitingProbe,
    ValidatingMedia,
    MediaValidationFailed,
    MediaValidated,
    PreprocessingMedia,
    MediaPreprocessingFailed,
    MediaPrepared,
    MediaIntegrityFailed,
}
