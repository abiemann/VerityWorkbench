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
        string readiness,
        int activeRecordingDependencyGroupCount,
        int activeUnassignedVideoCount,
        int sharedAssetGroupConflictCount)
    {
        Id = id;
        DisplayName = displayName;
        WorkspaceRoot = workspaceRoot;
        TruthfulVideoCount = truthfulVideoCount;
        DeceptionVideoCount = deceptionVideoCount;
        ArchivedVideoCount = archivedVideoCount;
        PendingMediaCount = pendingMediaCount;
        Readiness = readiness;
        ActiveRecordingDependencyGroupCount = activeRecordingDependencyGroupCount;
        ActiveUnassignedVideoCount = activeUnassignedVideoCount;
        SharedAssetGroupConflictCount = sharedAssetGroupConflictCount;
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

    public int ActiveRecordingDependencyGroupCount { get; }

    public int ActiveUnassignedVideoCount { get; }

    public int SharedAssetGroupConflictCount { get; }

    public string RecordingDependencyGroupSummary
    {
        get
        {
            var conflicts = SharedAssetGroupConflictCount == 0
                ? string.Empty
                : $" · {SharedAssetGroupConflictCount} shared-asset group conflict(s)";
            return $"{ActiveRecordingDependencyGroupCount} active recording dependency group(s) · "
                + $"{ActiveUnassignedVideoCount} active Unassigned selection(s)"
                + conflicts;
        }
    }

    public bool CanProcessData => Readiness is not "IngestingMedia"
        and not "ValidatingMedia"
        and not "PreprocessingMedia"
        and not "MediaIntegrityFailed"
        && (PendingMediaCount > 0
            || Readiness is "MediaIngestedAwaitingProbe"
                or "MediaValidationFailed"
                or "MediaValidated"
                or "MediaPreprocessingFailed");

    public string Status => _liveStatus ?? Readiness switch
    {
        "IngestingMedia" => "Media ingest in progress",
        "MediaIngestedAwaitingProbe" => "Media registered — awaiting validation",
        "ValidatingMedia" => "Media validation in progress",
        "MediaValidationFailed" => "Media validation needs attention",
        "MediaValidated" => "Media validated — awaiting preprocessing",
        "PreprocessingMedia" => "Media preprocessing in progress",
        "MediaPreprocessingFailed" => "Media preprocessing needs attention",
        "MediaPrepared" => "Media prepared — quality and applicability not assessed",
        "MediaIntegrityFailed" => "Workspace media changed — repair required",
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
