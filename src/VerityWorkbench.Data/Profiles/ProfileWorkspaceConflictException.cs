namespace VerityWorkbench.Data.Profiles;

public sealed class ProfileWorkspaceConflictException : InvalidOperationException
{
    public ProfileWorkspaceConflictException(string workspaceRoot, Exception innerException)
        : base($"The workspace '{workspaceRoot}' is already assigned to another profile.", innerException)
    {
        WorkspaceRoot = workspaceRoot;
    }

    public string WorkspaceRoot { get; }
}
