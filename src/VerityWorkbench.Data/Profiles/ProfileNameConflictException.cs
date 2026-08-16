namespace VerityWorkbench.Data.Profiles;

public sealed class ProfileNameConflictException : InvalidOperationException
{
    public ProfileNameConflictException(string displayName, Exception innerException)
        : base($"A profile named '{displayName}' already exists.", innerException)
    {
        DisplayName = displayName;
    }

    public string DisplayName { get; }
}
