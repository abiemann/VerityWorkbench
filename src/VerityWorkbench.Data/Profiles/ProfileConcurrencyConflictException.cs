namespace VerityWorkbench.Data.Profiles;

public sealed class ProfileConcurrencyConflictException : InvalidOperationException
{
    public ProfileConcurrencyConflictException(Guid profileId, DateTimeOffset expectedUpdatedAtUtc)
        : base("The profile was changed after it was opened. Reload it before saving again.")
    {
        ProfileId = profileId;
        ExpectedUpdatedAtUtc = expectedUpdatedAtUtc;
    }

    public Guid ProfileId { get; }

    public DateTimeOffset ExpectedUpdatedAtUtc { get; }
}
