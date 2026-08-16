using System.ComponentModel;
using System.Runtime.CompilerServices;
using VerityWorkbench.Core.Profiles;

namespace VerityWorkbench.App.ViewModels;

public sealed class TrainingVideoItemViewModel : INotifyPropertyChanged
{
    private string _recordingDateLabel = string.Empty;
    private bool _isArchived;

    public TrainingVideoItemViewModel(string fullPath, TrainingCondition condition)
        : this(
            Guid.NewGuid(),
            fullPath,
            condition,
            string.Empty,
            isArchived: false,
            isPersisted: false,
            mediaAssetId: null)
    {
    }

    public TrainingVideoItemViewModel(
        Guid id,
        string fullPath,
        TrainingCondition condition,
        string recordingDateLabel,
        bool isArchived,
        bool isPersisted,
        Guid? mediaAssetId)
    {
        Id = id;
        FullPath = Path.GetFullPath(fullPath);
        FileName = Path.GetFileName(FullPath);
        Condition = condition;
        _recordingDateLabel = recordingDateLabel;
        _isArchived = isArchived;
        IsPersisted = isPersisted;
        MediaAssetId = mediaAssetId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public string FileName { get; }

    public string FullPath { get; }

    public TrainingCondition Condition { get; }

    public bool IsPersisted { get; }

    public Guid? MediaAssetId { get; }

    public bool CanRemove => MediaAssetId is null;

    public string RecordingDateLabel
    {
        get => _recordingDateLabel;
        set
        {
            if (_recordingDateLabel == value)
            {
                return;
            }

            _recordingDateLabel = value;
            OnPropertyChanged();
        }
    }

    public bool IsArchived
    {
        get => _isArchived;
        set
        {
            if (_isArchived == value)
            {
                return;
            }

            _isArchived = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArchiveStatus));
            OnPropertyChanged(nameof(ArchiveActionLabel));
        }
    }

    public string ArchiveStatus => IsArchived ? "Archived" : "Active";

    public bool CanArchive => IsPersisted;

    public string ArchiveActionLabel => IsArchived ? "Unarchive" : "Archive";

    public LocalTrainingVideoSelection ToSelection() => new(
        FullPath,
        RecordingDateLabel,
        Condition,
        IsArchived);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
