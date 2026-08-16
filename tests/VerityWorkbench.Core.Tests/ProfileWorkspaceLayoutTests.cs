using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Core.Tests;

public sealed class ProfileWorkspaceLayoutTests
{
    [Fact]
    public void Download_staging_defaults_to_named_workspace_folder()
    {
        using var testDirectory = new TestDirectory();

        var layout = ProfileWorkspaceLayout.Create(testDirectory.Path);

        Assert.Equal(Path.Combine(testDirectory.Path, "Downloads"), layout.DownloadStagingRoot);
        Assert.Equal(
            Path.Combine(testDirectory.Path, "Profile", "profile.sqlite"),
            layout.ProfileDatabasePath);
    }

    [Fact]
    public void Explicit_download_staging_root_is_preserved()
    {
        using var testDirectory = new TestDirectory();
        var workspace = Path.Combine(testDirectory.Path, "workspace");
        var downloads = Path.Combine(testDirectory.Path, "staging");

        var layout = ProfileWorkspaceLayout.Create(workspace, downloads);

        Assert.Equal(Path.GetFullPath(downloads), layout.DownloadStagingRoot);
    }

    [Fact]
    public void Initializer_creates_only_the_named_top_level_workspace_folders()
    {
        using var testDirectory = new TestDirectory();
        var workspace = Path.Combine(testDirectory.Path, "workspace");
        var layout = ProfileWorkspaceLayout.Create(workspace);

        ProfileWorkspaceInitializer.Initialize(layout);

        var actualNames = Directory
            .GetDirectories(workspace)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expectedNames = ProfileWorkspaceLayout.TopLevelDirectoryNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedNames, actualNames);
    }
}
