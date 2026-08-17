using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VerityWorkbench.Core.Profiles;

namespace VerityWorkbench.App.ViewModels;

public sealed class TrainingVideoItemViewModel : INotifyPropertyChanged
{
    private string _recordingDateLabel = string.Empty;
    private bool _isArchived;
    private RecordingDependencyGroupOptionViewModel _selectedRecordingDependencyGroup;

    public TrainingVideoItemViewModel(
        string fullPath,
        TrainingCondition condition,
        ObservableCollection<RecordingDependencyGroupOptionViewModel> recordingDependencyGroupOptions)
        : this(
            Guid.NewGuid(),
            fullPath,
            condition,
            string.Empty,
            isArchived: false,
            isPersisted: false,
            mediaAssetId: null,
            recordingDependencyGroupId: null,
            recordingDependencyGroupOptions)
    {
    }

    public TrainingVideoItemViewModel(
        Guid id,
        string fullPath,
        TrainingCondition condition,
        string recordingDateLabel,
        bool isArchived,
        bool isPersisted,
        Guid? mediaAssetId,
        Guid? recordingDependencyGroupId,
        ObservableCollection<RecordingDependencyGroupOptionViewModel> recordingDependencyGroupOptions)
    {
        Id = id;
        FullPath = Path.GetFullPath(fullPath);
        FileName = Path.GetFileName(FullPath);
        Condition = condition;
        _recordingDateLabel = recordingDateLabel;
        _isArchived = isArchived;
        IsPersisted = isPersisted;
        MediaAssetId = mediaAssetId;
        RecordingDependencyGroupOptions = recordingDependencyGroupOptions;
        _selectedRecordingDependencyGroup = recordingDependencyGroupOptions.FirstOrDefault(option =>
                option.Id == recordingDependencyGroupId)
            ?? throw new ArgumentException(
                "The selected recording dependency group is not available.",
                nameof(recordingDependencyGroupId));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public string FileName { get; }

    public string FullPath { get; }

    public TrainingCondition Condition { get; }

    public bool IsPersisted { get; }

    public Guid? MediaAssetId { get; }

    public ObservableCollection<RecordingDependencyGroupOptionViewModel> RecordingDependencyGroupOptions { get; }

    public RecordingDependencyGroupOptionViewModel SelectedRecordingDependencyGroup
    {
        get => _selectedRecordingDependencyGroup;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_selectedRecordingDependencyGroup, value))
            {
                return;
            }

            _selectedRecordingDependencyGroup = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecordingDependencyGroupId));
        }
    }

    public Guid? RecordingDependencyGroupId => SelectedRecordingDependencyGroup.Id;

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
        IsArchived,
        RecordingDependencyGroupId);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
