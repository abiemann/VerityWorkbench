namespace VerityWorkbench.Media;

public sealed record StagedLocalMediaItem(
    Guid JobId,
    Guid TrainingVideoId,
    string Sha256,
    long ByteLength,
    string SourceFileName,
    string StagedDirectoryPath,
    string StagedFilePath);
