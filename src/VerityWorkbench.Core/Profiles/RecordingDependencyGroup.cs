namespace VerityWorkbench.Core.Profiles;

/// <summary>
/// A user-defined dependency boundary for recordings that must remain together
/// during future model-development splits. The display name is metadata only.
/// </summary>
public sealed record RecordingDependencyGroup(
    Guid Id,
    string DisplayName);
