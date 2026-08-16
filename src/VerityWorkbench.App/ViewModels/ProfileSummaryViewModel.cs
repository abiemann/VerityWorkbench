using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VerityWorkbench.App.ViewModels;

public sealed class ProfileSummaryViewModel : INotifyPropertyChanged
{
    private string? _liveStatus;

    public ProfileSummaryViewModel(
        Guid id,
        string displayName,
        string workspaceRoot,
        int truthfulVideoCount,
        int deceptionVideoCount,
        int archivedVideoCount,
        int pendingMediaCount,
        string readiness)
    {
        Id = id;
        DisplayName = displayName;
        WorkspaceRoot = workspaceRoot;
        TruthfulVideoCount = truthfulVideoCount;
        DeceptionVideoCount = deceptionVideoCount;
        ArchivedVideoCount = archivedVideoCount;
        PendingMediaCount = pendingMediaCount;
        Readiness = readiness;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public string DisplayName { get; }

    public string WorkspaceRoot { get; }

    public int TruthfulVideoCount { get; }

    public int DeceptionVideoCount { get; }

    public int ArchivedVideoCount { get; }

    public int PendingMediaCount { get; }

    public string Readiness { get; }

    public bool CanStartIngest => Readiness != "IngestingMedia"
        && (PendingMediaCount > 0 || Readiness == "MediaIngestedAwaitingProbe");

    public string Status => _liveStatus ?? Readiness switch
    {
        "IngestingMedia" => "Media ingest in progress",
        "MediaIngestedAwaitingProbe" => "Media registered — awaiting validation",
        _ => "Draft — not processed",
    };

    public string TrainingSummary
    {
        get
        {
            var active = $"{TruthfulVideoCount} verified sincere-truth MP4(s) · {DeceptionVideoCount} verified intentional-deception MP4(s)";
            var archived = ArchivedVideoCount == 0 ? string.Empty : $" · {ArchivedVideoCount} archived";
            var pending = PendingMediaCount == 0 ? string.Empty : $" · {PendingMediaCount} awaiting ingest";
            return active + archived + pending;
        }
    }

    public void SetLiveStatus(string? status)
    {
        if (_liveStatus == status)
        {
            return;
        }

        _liveStatus = status;
        OnPropertyChanged(nameof(Status));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
