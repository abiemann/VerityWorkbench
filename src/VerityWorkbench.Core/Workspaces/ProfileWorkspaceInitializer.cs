namespace VerityWorkbench.Core.Workspaces;

public static class ProfileWorkspaceInitializer
{
    public static void Initialize(ProfileWorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        Directory.CreateDirectory(layout.WorkspaceRoot);
        foreach (var directory in layout.GetDirectoriesToCreate())
        {
            Directory.CreateDirectory(directory);
        }
    }
}

