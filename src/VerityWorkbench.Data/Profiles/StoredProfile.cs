namespace VerityWorkbench.Data.Profiles;

public sealed record StoredProfile(
    Guid Id,
    string DisplayName,
    string WorkspaceRoot,
    string? DownloadStagingRoot,
    string Readiness,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<StoredTrainingVideo> TrainingVideos,
    IReadOnlyList<StoredRecordingDependencyGroup> RecordingDependencyGroups)
{
    public StoredProfile(
        Guid id,
        string displayName,
        string workspaceRoot,
        string? downloadStagingRoot,
        string readiness,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        IReadOnlyList<StoredTrainingVideo> trainingVideos)
        : this(
            id,
            displayName,
            workspaceRoot,
            downloadStagingRoot,
            readiness,
            createdAtUtc,
            updatedAtUtc,
            trainingVideos,
            [])
    {
    }
}
