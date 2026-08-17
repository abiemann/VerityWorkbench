using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Core.Profiles;

public static class ProfileDraftValidator
{
    public static IReadOnlyList<ProfileValidationIssue> Validate(
        ProfileDraft draft,
        bool requireActiveInput = true,
        bool validateSourceExistence = true)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var issues = new List<ProfileValidationIssue>();

        if (string.IsNullOrWhiteSpace(draft.DisplayName))
        {
            issues.Add(new("ProfileName.Required", "Enter a pseudonymous profile name."));
        }

        ValidateWorkspaceRoot(draft.WorkspaceRoot, "Workspace", issues);

        if (!string.IsNullOrWhiteSpace(draft.DownloadStagingRoot))
        {
            ValidateWorkspaceRoot(draft.DownloadStagingRoot, "Download staging", issues);
        }

        ValidateSelections(draft.TrainingVideos, validateSourceExistence, issues);
        ValidateRecordingDependencyGroups(
            draft.RecordingDependencyGroups,
            draft.TrainingVideos,
            issues);
        ValidateImportedPackage(draft.ImportedPackagePath, issues);

        if (requireActiveInput
            && draft.TrainingVideos.All(selection => selection.IsArchived)
            && string.IsNullOrWhiteSpace(draft.ImportedPackagePath))
        {
            issues.Add(new(
                "Input.Required",
                "Add at least one local MP4 or import a compatible .vwpkg package."));
        }

        return issues;
    }

    private static void ValidateRecordingDependencyGroups(
        IReadOnlyList<RecordingDependencyGroup> groups,
        IReadOnlyList<LocalTrainingVideoSelection> selections,
        ICollection<ProfileValidationIssue> issues)
    {
        var groupIds = new HashSet<Guid>();
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group.Id == Guid.Empty)
            {
                issues.Add(new(
                    "RecordingDependencyGroup.IdRequired",
                    "A recording dependency group has no stable ID."));
            }
            else if (!groupIds.Add(group.Id))
            {
                issues.Add(new(
                    "RecordingDependencyGroup.DuplicateId",
                    "A recording dependency group ID appears more than once."));
            }

            if (string.IsNullOrWhiteSpace(group.DisplayName))
            {
                issues.Add(new(
                    "RecordingDependencyGroup.NameRequired",
                    "Enter a name for every recording dependency group."));
                continue;
            }

            if (string.Equals(group.DisplayName, "Unassigned", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(
                    "RecordingDependencyGroup.NameReserved",
                    "Unassigned is reserved for videos without a recording dependency group."));
                continue;
            }

            if (group.DisplayName.Length > 200
                || !string.Equals(group.DisplayName, group.DisplayName.Trim(), StringComparison.Ordinal)
                || group.DisplayName.Any(char.IsControl))
            {
                issues.Add(new(
                    "RecordingDependencyGroup.NameInvalid",
                    "Recording dependency group names must be trimmed, contain no control characters, and be 200 characters or fewer."));
                continue;
            }

            if (!groupNames.Add(group.DisplayName))
            {
                issues.Add(new(
                    "RecordingDependencyGroup.DuplicateName",
                    $"Recording dependency group names must be unique: {group.DisplayName}"));
            }
        }

        foreach (var selection in selections)
        {
            if (selection.RecordingDependencyGroupId == Guid.Empty
                || selection.RecordingDependencyGroupId is Guid groupId && !groupIds.Contains(groupId))
            {
                issues.Add(new(
                    "TrainingVideo.RecordingDependencyGroupUnknown",
                    "A training video references a recording dependency group that does not exist in this profile."));
            }
        }
    }

    private static void ValidateWorkspaceRoot(
        string? root,
        string label,
        ICollection<ProfileValidationIssue> issues)
    {
        var result = WorkspacePathPolicy.Validate(root);
        if (!result.IsValid)
        {
            issues.Add(new($"{label.Replace(" ", string.Empty)}.Invalid", $"{label}: {result.Error}"));
        }
    }

    private static void ValidateSelections(
        IReadOnlyList<LocalTrainingVideoSelection> selections,
        bool validateSourceExistence,
        ICollection<ProfileValidationIssue> issues)
    {
        var canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var selection in selections)
        {
            if (string.IsNullOrWhiteSpace(selection.FilePath))
            {
                issues.Add(new("TrainingVideo.PathRequired", "A training video has no local file path."));
                continue;
            }

            if (!string.Equals(Path.GetExtension(selection.FilePath), ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(
                    "TrainingVideo.Mp4Required",
                    $"Only MP4 is supported in version 1: {selection.FilePath}"));
                continue;
            }

            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(selection.FilePath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                issues.Add(new("TrainingVideo.InvalidPath", $"Invalid video path: {selection.FilePath}"));
                continue;
            }

            if (validateSourceExistence && !selection.IsArchived && !File.Exists(canonicalPath))
            {
                issues.Add(new("TrainingVideo.NotFound", $"Training video not found: {canonicalPath}"));
            }

            if (!canonicalPaths.Add(canonicalPath))
            {
                issues.Add(new(
                    "TrainingVideo.Duplicate",
                    $"A local MP4 can appear only once across both training lists: {canonicalPath}"));
            }
        }
    }

    private static void ValidateImportedPackage(
        string? importedPackagePath,
        ICollection<ProfileValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(importedPackagePath))
        {
            return;
        }

        if (!string.Equals(Path.GetExtension(importedPackagePath), ".vwpkg", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("ImportedPackage.Extension", "An imported model package must have the .vwpkg extension."));
            return;
        }

        if (!File.Exists(importedPackagePath))
        {
            issues.Add(new("ImportedPackage.NotFound", $"Model package not found: {importedPackagePath}"));
        }
    }
}
