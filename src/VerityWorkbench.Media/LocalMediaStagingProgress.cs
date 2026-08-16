namespace VerityWorkbench.Media;

public sealed record LocalMediaStagingProgress(
    Guid JobId,
    Guid TrainingVideoId,
    int ItemNumber,
    int ItemCount,
    long BytesCopied,
    long TotalBytes);
