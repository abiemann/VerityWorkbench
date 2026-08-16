namespace VerityWorkbench.Data.Profiles;

public sealed class MediaPreprocessingConflictException : InvalidOperationException
{
    public MediaPreprocessingConflictException(Guid mediaAssetId)
        : base($"Media asset '{mediaAssetId}' already has an immutable preprocessing result.")
    {
        MediaAssetId = mediaAssetId;
    }

    public Guid MediaAssetId { get; }
}
