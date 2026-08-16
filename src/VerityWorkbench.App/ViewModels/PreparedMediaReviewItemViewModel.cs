using VerityWorkbench.Core.Profiles;

namespace VerityWorkbench.App.ViewModels;

public sealed class PreparedMediaReviewItemViewModel
{
    public PreparedMediaReviewItemViewModel(
        Guid mediaAssetId,
        TrainingCondition condition,
        IReadOnlyList<string> recordingLabels,
        int linkedSelectionCount,
        long durationMicroseconds,
        long sourceTimelineOriginMicroseconds)
    {
        if (mediaAssetId == Guid.Empty)
        {
            throw new ArgumentException("The prepared media asset ID cannot be empty.", nameof(mediaAssetId));
        }

        ArgumentNullException.ThrowIfNull(recordingLabels);
        if (recordingLabels.Count == 0)
        {
            throw new ArgumentException("At least one recording label is required.", nameof(recordingLabels));
        }

        if (linkedSelectionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(linkedSelectionCount));
        }

        if (durationMicroseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMicroseconds));
        }

        MediaAssetId = mediaAssetId;
        Condition = condition;
        RecordingLabels = recordingLabels.ToArray();
        LinkedSelectionCount = linkedSelectionCount;
        DurationMicroseconds = durationMicroseconds;
        SourceTimelineOriginMicroseconds = sourceTimelineOriginMicroseconds;
    }

    public Guid MediaAssetId { get; }

    public TrainingCondition Condition { get; }

    public IReadOnlyList<string> RecordingLabels { get; }

    public int LinkedSelectionCount { get; }

    public long DurationMicroseconds { get; }

    public long SourceTimelineOriginMicroseconds { get; }

    public string DisplayTitle => RecordingLabels[0];

    public string RecordingLabelsText => RecordingLabels.Count == 1
        ? $"Recording label: {RecordingLabels[0]}"
        : "Recording labels: " + string.Join(" · ", RecordingLabels);

    public string ConditionText => Condition switch
    {
        TrainingCondition.VerifiedSincereTruth => "Verified sincere-truth training media",
        TrainingCondition.VerifiedIntentionalDeception =>
            "Verified intentional-deception training media",
        _ => "Unknown training condition",
    };

    public string ReuseText => LinkedSelectionCount == 1
        ? "1 active training selection"
        : $"{LinkedSelectionCount} active selections share these same media bytes";

    public string DurationText => "Duration: " + FormatDuration(DurationMicroseconds);

    public string AssetReferenceText => "Prepared asset " + MediaAssetId.ToString("N")[..12];

    private static string FormatDuration(long microseconds)
    {
        var duration = TimeSpan.FromTicks(checked(microseconds * 10));
        var totalHours = (long)duration.TotalHours;
        return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";
    }
}
