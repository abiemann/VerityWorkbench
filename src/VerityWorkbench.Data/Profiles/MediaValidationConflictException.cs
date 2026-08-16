namespace VerityWorkbench.Data.Profiles;

public sealed class MediaValidationConflictException : InvalidOperationException
{
    public MediaValidationConflictException(Guid mediaAssetId)
        : base($"Media asset '{mediaAssetId}' already has an immutable successful validation result.")
    {
        MediaAssetId = mediaAssetId;
    }

    public Guid MediaAssetId { get; }
}
