namespace VerityWorkbench.Core.Workspaces;

public static class WorkspacePathPolicy
{
    public static WorkspacePathValidationResult Validate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Invalid("select a folder.");
        }

        var trimmed = candidate.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            return Invalid("the folder must be an absolute path.");
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid("the folder path is invalid.");
        }

        var pathRoot = Path.GetPathRoot(normalized);
        if (pathRoot is not null && string.Equals(
                normalized,
                Path.TrimEndingDirectorySeparator(pathRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("a drive or share root cannot be used as a profile workload.");
        }

        if (File.Exists(normalized))
        {
            return Invalid("the selected path is a file, not a folder.");
        }

        return new(true, normalized, null);
    }

    private static WorkspacePathValidationResult Invalid(string error) => new(false, null, error);
}

