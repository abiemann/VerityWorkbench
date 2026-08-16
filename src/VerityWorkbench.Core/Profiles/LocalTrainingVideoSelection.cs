namespace VerityWorkbench.Core.Profiles;

/// <summary>
/// An explicitly selected local training video. RecordingDateLabel is opaque
/// display/sort text and must never be used as a feature, label, or identity.
/// </summary>
public sealed record LocalTrainingVideoSelection(
    string FilePath,
    string RecordingDateLabel,
    TrainingCondition Condition,
    bool IsArchived = false);
