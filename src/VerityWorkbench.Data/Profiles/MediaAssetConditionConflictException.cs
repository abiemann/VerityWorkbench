using VerityWorkbench.Core.Profiles;

namespace VerityWorkbench.Data.Profiles;

public sealed class MediaAssetConditionConflictException : InvalidOperationException
{
    public MediaAssetConditionConflictException(
        string sha256,
        TrainingCondition existingCondition,
        TrainingCondition requestedCondition)
        : base(
            $"Media content '{sha256}' cannot be registered as both " +
            $"'{existingCondition}' and '{requestedCondition}'.")
    {
        Sha256 = sha256;
        ExistingCondition = existingCondition;
        RequestedCondition = requestedCondition;
    }

    public string Sha256 { get; }

    public TrainingCondition ExistingCondition { get; }

    public TrainingCondition RequestedCondition { get; }
}
