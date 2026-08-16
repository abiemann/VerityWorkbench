namespace VerityWorkbench.Core.Workspaces;

public sealed class ProfileWorkspaceLayout
{
    public static readonly IReadOnlyList<string> TopLevelDirectoryNames =
    [
        "Profile",
        "Media",
        "Downloads",
        "Processing",
        "Features",
        "Models",
        "Exports",
        "Reports",
    ];

    private ProfileWorkspaceLayout(string workspaceRoot, string downloadStagingRoot)
    {
        WorkspaceRoot = workspaceRoot;
        ProfileRoot = Path.Combine(workspaceRoot, "Profile");
        ProfileDatabasePath = Path.Combine(ProfileRoot, "profile.sqlite");
        MediaRoot = Path.Combine(workspaceRoot, "Media");
        WorkspaceDownloadsRoot = Path.Combine(workspaceRoot, "Downloads");
        DownloadStagingRoot = downloadStagingRoot;
        ProcessingRoot = Path.Combine(workspaceRoot, "Processing");
        FeaturesRoot = Path.Combine(workspaceRoot, "Features");
        ModelsRoot = Path.Combine(workspaceRoot, "Models");
        ExportsRoot = Path.Combine(workspaceRoot, "Exports");
        ReportsRoot = Path.Combine(workspaceRoot, "Reports");
    }

    public string WorkspaceRoot { get; }

    public string ProfileRoot { get; }

    public string ProfileDatabasePath { get; }

    public string MediaRoot { get; }

    public string WorkspaceDownloadsRoot { get; }

    public string DownloadStagingRoot { get; }

    public string ProcessingRoot { get; }

    public string FeaturesRoot { get; }

    public string ModelsRoot { get; }

    public string ExportsRoot { get; }

    public string ReportsRoot { get; }

    public static ProfileWorkspaceLayout Create(string workspaceRoot, string? downloadStagingRoot = null)
    {
        var workspaceResult = WorkspacePathPolicy.Validate(workspaceRoot);
        if (!workspaceResult.IsValid)
        {
            throw new ArgumentException(workspaceResult.Error, nameof(workspaceRoot));
        }

        var resolvedDownloadRoot = Path.Combine(workspaceResult.NormalizedPath!, "Downloads");
        if (!string.IsNullOrWhiteSpace(downloadStagingRoot))
        {
            var downloadResult = WorkspacePathPolicy.Validate(downloadStagingRoot);
            if (!downloadResult.IsValid)
            {
                throw new ArgumentException(downloadResult.Error, nameof(downloadStagingRoot));
            }

            resolvedDownloadRoot = downloadResult.NormalizedPath!;
        }

        return new(workspaceResult.NormalizedPath!, resolvedDownloadRoot);
    }

    public IReadOnlyList<string> GetDirectoriesToCreate()
    {
        var directories = TopLevelDirectoryNames
            .Select(name => Path.Combine(WorkspaceRoot, name))
            .ToList();

        if (!directories.Contains(DownloadStagingRoot, StringComparer.OrdinalIgnoreCase))
        {
            directories.Add(DownloadStagingRoot);
        }

        return directories;
    }
}
