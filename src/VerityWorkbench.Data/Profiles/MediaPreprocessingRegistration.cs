namespace VerityWorkbench.Data.Profiles;

public sealed record MediaPreprocessingRegistration(
    Guid MediaAssetId,
    MediaAssetState State,
    StoredMediaPreprocessingResult? Result,
    string? FailureMessage);
