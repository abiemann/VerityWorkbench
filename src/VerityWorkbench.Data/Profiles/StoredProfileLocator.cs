namespace VerityWorkbench.Data.Profiles;

public sealed record StoredProfileLocator(
    Guid ProfileId,
    string WorkspaceRoot,
    DateTimeOffset AddedAtUtc,
    ProfileLocatorState State = ProfileLocatorState.Ready);
