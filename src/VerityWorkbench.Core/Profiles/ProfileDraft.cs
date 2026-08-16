namespace VerityWorkbench.Core.Profiles;

public sealed class ProfileDraft
{
    public ProfileDraft(
        string displayName,
        string workspaceRoot,
        string? downloadStagingRoot,
        IEnumerable<LocalTrainingVideoSelection> trainingVideos,
        string? importedPackagePath = null)
    {
        DisplayName = displayName;
        WorkspaceRoot = workspaceRoot;
        DownloadStagingRoot = downloadStagingRoot;
        TrainingVideos = trainingVideos?.ToArray()
            ?? throw new ArgumentNullException(nameof(trainingVideos));
        ImportedPackagePath = importedPackagePath;
    }

    public string DisplayName { get; }

    public string WorkspaceRoot { get; }

    public string? DownloadStagingRoot { get; }

    public IReadOnlyList<LocalTrainingVideoSelection> TrainingVideos { get; }

    public string? ImportedPackagePath { get; }

    public ProfileReadiness Readiness => ProfileReadiness.Draft;
}
