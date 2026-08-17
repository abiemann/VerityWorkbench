using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using VerityWorkbench.Data.Profiles;

namespace VerityWorkbench.App.ViewModels;

internal sealed class ProcessingHistoryItemViewModel : INotifyPropertyChanged
{
    private DateTimeOffset? _workspaceCleanedAtUtc;
    private bool _isBusy;
    private string? _folderNotice;

    public ProcessingHistoryItemViewModel(StoredProcessingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        Id = job.Id;
        ProfileId = job.ProfileId;
        Kind = job.Kind;
        State = job.State;
        CompletedItemCount = job.CompletedItemCount;
        TotalItemCount = job.TotalItemCount;
        CompletedBytes = job.CompletedBytes;
        TotalBytes = job.TotalBytes;
        WorkspaceRelativePath = job.WorkspaceRelativePath;
        Error = job.Error;
        CreatedAtUtc = job.CreatedAtUtc;
        UpdatedAtUtc = job.UpdatedAtUtc;
        _workspaceCleanedAtUtc = job.WorkspaceCleanedAtUtc;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public Guid ProfileId { get; }

    public ProcessingJobKind Kind { get; }

    public ProcessingJobState State { get; }

    public int CompletedItemCount { get; }

    public int TotalItemCount { get; }

    public long CompletedBytes { get; }

    public long TotalBytes { get; }

    public string WorkspaceRelativePath { get; }

    public string? Error { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public DateTimeOffset? WorkspaceCleanedAtUtc => _workspaceCleanedAtUtc;

    public string KindText => Kind switch
    {
        ProcessingJobKind.LocalMediaIngest => "Local media ingest",
        ProcessingJobKind.MediaValidation => "MP4 media validation",
        ProcessingJobKind.MediaPreprocessing => "Deterministic media preprocessing",
        ProcessingJobKind.AudioObservationExtraction => "Objective audio observations",
        _ => "Processing job",
    };

    public string OutcomeText => State switch
    {
        ProcessingJobState.Queued => "Queued",
        ProcessingJobState.Running => "Running",
        ProcessingJobState.Completed => "Completed",
        ProcessingJobState.Cancelled => "Cancelled",
        ProcessingJobState.Failed => "Failed",
        ProcessingJobState.Interrupted => "Interrupted",
        _ => "Unknown state",
    };

    public string ProgressText
    {
        get
        {
            var itemProgress = TotalItemCount == 0
                ? "No item total recorded"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{CompletedItemCount:N0} of {TotalItemCount:N0} item(s)");
            if (TotalBytes == 0)
            {
                return itemProgress;
            }

            return itemProgress + string.Create(
                CultureInfo.InvariantCulture,
                $" · {FormatBytes(CompletedBytes)} of {FormatBytes(TotalBytes)}");
        }
    }

    public string CreatedText => "Created " + FormatLocalTimestamp(CreatedAtUtc);

    public string UpdatedText => "Last updated " + FormatLocalTimestamp(UpdatedAtUtc);

    public string ErrorText => string.IsNullOrWhiteSpace(Error)
        ? "No failure message recorded."
        : "Recorded failure: " + Error;

    public string CleanupStatusText
    {
        get
        {
            if (_workspaceCleanedAtUtc is { } cleanedAtUtc)
            {
                return "Processing data deleted " + FormatLocalTimestamp(cleanedAtUtc);
            }

            if (!IsTerminal)
            {
                return "Active job folder — deletion unavailable";
            }

            return _folderNotice ?? "Retained processing folder";
        }
    }

    public bool CanOpenFolder => !_isBusy && _workspaceCleanedAtUtc is null;

    public bool CanDeleteProcessingData => !_isBusy && IsTerminal && _workspaceCleanedAtUtc is null;

    public bool Matches(StoredProcessingJob job) =>
        job.Id == Id
        && job.ProfileId == ProfileId
        && job.Kind == Kind
        && job.State == State
        && string.Equals(
            job.WorkspaceRelativePath,
            WorkspaceRelativePath,
            StringComparison.Ordinal)
        && job.WorkspaceCleanedAtUtc == WorkspaceCleanedAtUtc;

    public void SetBusy(bool isBusy)
    {
        if (_isBusy == isBusy)
        {
            return;
        }

        _isBusy = isBusy;
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(CanDeleteProcessingData));
    }

    public void SetFolderNotice(string? notice)
    {
        if (string.Equals(_folderNotice, notice, StringComparison.Ordinal))
        {
            return;
        }

        _folderNotice = notice;
        OnPropertyChanged(nameof(CleanupStatusText));
    }

    public void MarkWorkspaceCleaned(DateTimeOffset cleanedAtUtc)
    {
        _workspaceCleanedAtUtc = cleanedAtUtc;
        _folderNotice = null;
        OnPropertyChanged(nameof(WorkspaceCleanedAtUtc));
        OnPropertyChanged(nameof(CleanupStatusText));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(CanDeleteProcessingData));
    }

    private bool IsTerminal => State is ProcessingJobState.Completed
        or ProcessingJobState.Cancelled
        or ProcessingJobState.Failed
        or ProcessingJobState.Interrupted;

    private static string FormatLocalTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);

    private static string FormatBytes(long byteCount)
    {
        if (byteCount < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{byteCount:N0} B");
        }

        var value = (double)byteCount;
        string[] suffixes = ["KiB", "MiB", "GiB", "TiB"];
        foreach (var suffix in suffixes)
        {
            value /= 1024d;
            if (value < 1024d || suffix == suffixes[^1])
            {
                return string.Create(CultureInfo.InvariantCulture, $"{value:N1} {suffix}");
            }
        }

        throw new InvalidOperationException("The byte count could not be formatted.");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
