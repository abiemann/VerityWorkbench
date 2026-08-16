namespace VerityWorkbench.Media;

public sealed record PromotedLocalMediaAsset(
    Guid AssetId,
    Guid JobId,
    Guid TrainingVideoId,
    string Sha256,
    long ByteLength,
    string AssetDirectoryPath,
    string OriginalFilePath,
    string WorkspaceRelativeOriginalPath,
    string OriginatingStagedDirectoryPath);
