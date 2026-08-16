namespace VerityWorkbench.Data.Profiles;

public sealed record StoredProcessingJob(
    Guid Id,
    Guid ProfileId,
    ProcessingJobKind Kind,
    ProcessingJobState State,
    int CompletedItemCount,
    int TotalItemCount,
    long CompletedBytes,
    long TotalBytes,
    string WorkspaceRelativePath,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
