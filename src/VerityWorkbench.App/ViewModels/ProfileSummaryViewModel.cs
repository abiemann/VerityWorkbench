namespace VerityWorkbench.App.ViewModels;

public sealed class ProfileSummaryViewModel
{
    public ProfileSummaryViewModel(
        Guid id,
        string displayName,
        string workspaceRoot,
        int truthfulVideoCount,
        int deceptionVideoCount,
        int archivedVideoCount)
    {
        Id = id;
        DisplayName = displayName;
        WorkspaceRoot = workspaceRoot;
        TruthfulVideoCount = truthfulVideoCount;
        DeceptionVideoCount = deceptionVideoCount;
        ArchivedVideoCount = archivedVideoCount;
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    public string WorkspaceRoot { get; }

    public int TruthfulVideoCount { get; }

    public int DeceptionVideoCount { get; }

    public int ArchivedVideoCount { get; }

    public string Status => "Draft — not processed";

    public string TrainingSummary
    {
        get
        {
            var active = $"{TruthfulVideoCount} verified sincere-truth MP4(s) · {DeceptionVideoCount} verified intentional-deception MP4(s)";
            return ArchivedVideoCount == 0 ? active : $"{active} · {ArchivedVideoCount} archived";
        }
    }
}
