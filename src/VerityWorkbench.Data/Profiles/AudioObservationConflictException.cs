namespace VerityWorkbench.Data.Profiles;

public sealed class AudioObservationConflictException : InvalidOperationException
{
    public AudioObservationConflictException(Guid mediaAssetId)
        : base($"Media asset '{mediaAssetId}' already has an immutable audio-observation result or is no longer eligible for this job.")
    {
        MediaAssetId = mediaAssetId;
    }

    public Guid MediaAssetId { get; }
}
