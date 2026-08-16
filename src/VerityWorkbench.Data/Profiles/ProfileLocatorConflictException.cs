namespace VerityWorkbench.Data.Profiles;

public sealed class ProfileLocatorConflictException : InvalidOperationException
{
    public ProfileLocatorConflictException(string workspaceRoot, Exception? innerException = null)
        : base(
            "A profile locator with this ID or an overlapping workspace already exists.",
            innerException)
    {
        WorkspaceRoot = workspaceRoot;
    }

    public string WorkspaceRoot { get; }
}
