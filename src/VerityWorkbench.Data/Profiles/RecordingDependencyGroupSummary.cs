namespace VerityWorkbench.Data.Profiles;

public sealed record RecordingDependencyGroupConflict(
    Guid MediaAssetId,
    IReadOnlyList<Guid> RecordingDependencyGroupIds);

public sealed record RecordingDependencyGroupSummary(
    int ActiveAssignedGroupCount,
    int ActiveUnassignedVideoCount,
    IReadOnlyList<RecordingDependencyGroupConflict> Conflicts);

public static class RecordingDependencyGroupSummaryBuilder
{
    public static RecordingDependencyGroupSummary Create(StoredProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var activeVideos = profile.TrainingVideos
            .Where(video => !video.IsArchived)
            .ToArray();
        var assignedGroupCount = activeVideos
            .Where(video => video.RecordingDependencyGroupId.HasValue)
            .Select(video => video.RecordingDependencyGroupId!.Value)
            .Distinct()
            .Count();
        var unassignedCount = activeVideos.Count(video => !video.RecordingDependencyGroupId.HasValue);
        var conflicts = activeVideos
            .Where(video => video.MediaAssetId.HasValue && video.RecordingDependencyGroupId.HasValue)
            .GroupBy(video => video.MediaAssetId!.Value)
            .Select(group => new RecordingDependencyGroupConflict(
                group.Key,
                group.Select(video => video.RecordingDependencyGroupId!.Value)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray()))
            .Where(conflict => conflict.RecordingDependencyGroupIds.Count > 1)
            .OrderBy(conflict => conflict.MediaAssetId)
            .ToArray();

        return new(assignedGroupCount, unassignedCount, conflicts);
    }
}
