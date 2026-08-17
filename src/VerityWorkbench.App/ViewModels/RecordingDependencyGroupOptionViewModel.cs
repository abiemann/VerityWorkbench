using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VerityWorkbench.App.ViewModels;

public sealed class RecordingDependencyGroupOptionViewModel : INotifyPropertyChanged
{
    private string _displayName;

    public RecordingDependencyGroupOptionViewModel(Guid? id, string displayName)
    {
        Id = id;
        _displayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid? Id { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value)
            {
                return;
            }

            _displayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }
}
