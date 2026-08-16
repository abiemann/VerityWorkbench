using VerityWorkbench.Core.Profiles;

namespace VerityWorkbench.Data.Profiles;

public sealed record StoredTrainingVideo(
    Guid Id,
    string FilePath,
    string RecordingDateLabel,
    TrainingCondition Condition,
    bool IsArchived,
    int SortOrder,
    Guid? MediaAssetId = null);
