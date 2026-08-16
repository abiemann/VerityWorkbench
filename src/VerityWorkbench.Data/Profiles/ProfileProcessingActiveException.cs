namespace VerityWorkbench.Data.Profiles;

public sealed class ProfileProcessingActiveException : InvalidOperationException
{
    public ProfileProcessingActiveException(Guid profileId)
        : base($"Profile '{profileId}' has an active processing job.")
    {
        ProfileId = profileId;
    }

    public Guid ProfileId { get; }
}
