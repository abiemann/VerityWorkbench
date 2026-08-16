namespace VerityWorkbench.Data.Profiles;

public sealed record MediaValidationRegistration(
    Guid MediaAssetId,
    MediaAssetState State,
    StoredMediaValidationResult? Result,
    string? FailureMessage);
