namespace VerityWorkbench.Data.Profiles;

public sealed record MediaAssetRegistration(
    Guid TrainingVideoId,
    Guid MediaAssetId,
    string Sha256,
    string WorkspaceRelativePath,
    long ByteLength);
