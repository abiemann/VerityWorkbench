namespace VerityWorkbench.Data.Profiles;

public sealed record AudioObservationRegistration(
    Guid MediaAssetId,
    StoredAudioObservationResult? Result,
    string? FailureMessage);
