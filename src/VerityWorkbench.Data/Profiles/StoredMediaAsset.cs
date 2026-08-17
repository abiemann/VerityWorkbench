namespace VerityWorkbench.Data.Profiles;

public sealed record StoredMediaAsset(
    Guid Id,
    Guid ProfileId,
    string Sha256,
    string WorkspaceRelativePath,
    long ByteLength,
    MediaAssetState State,
    string? ValidationFailure,
    string? PreprocessingFailure,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? AudioObservationFailure = null);
