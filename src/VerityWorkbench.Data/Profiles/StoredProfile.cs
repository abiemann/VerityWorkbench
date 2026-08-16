namespace VerityWorkbench.Data.Profiles;

public sealed record StoredProfile(
    Guid Id,
    string DisplayName,
    string WorkspaceRoot,
    string? DownloadStagingRoot,
    string Readiness,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<StoredTrainingVideo> TrainingVideos);
