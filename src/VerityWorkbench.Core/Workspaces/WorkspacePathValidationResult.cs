namespace VerityWorkbench.Core.Workspaces;

public sealed record WorkspacePathValidationResult(bool IsValid, string? NormalizedPath, string? Error);

