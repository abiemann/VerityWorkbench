using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "VerityWorkbench.Media.Tests",
            Guid.NewGuid().ToString("N"));
        Layout = ProfileWorkspaceLayout.Create(Path.Combine(Root, "workspace"));
        ProfileWorkspaceInitializer.Initialize(Layout);
        Sources = Path.Combine(Root, "sources");
        Directory.CreateDirectory(Sources);
    }

    public string Root { get; }

    public string Sources { get; }

    public ProfileWorkspaceLayout Layout { get; }

    public string CreateSource(string fileName, byte[] bytes)
    {
        var path = Path.Combine(Sources, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
