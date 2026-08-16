namespace VerityWorkbench.Media;

public sealed record LocalMediaStagingJobResult(
    Guid JobId,
    DateTimeOffset CreatedAtUtc,
    string JobRelativePath,
    string JobDirectoryPath,
    IReadOnlyList<StagedLocalMediaItem> Items);
