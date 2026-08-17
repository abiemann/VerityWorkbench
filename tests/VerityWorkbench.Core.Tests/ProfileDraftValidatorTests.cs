using VerityWorkbench.Core.Profiles;

namespace VerityWorkbench.Core.Tests;

public sealed class ProfileDraftValidatorTests
{
    [Fact]
    public void Blank_profile_name_is_rejected()
    {
        using var testDirectory = new TestDirectory();
        var video = testDirectory.CreateFile("truth.mp4");
        var draft = CreateDraft(testDirectory.Path, video, displayName: "   ");

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Contains(issues, issue => issue.Code == "ProfileName.Required");
    }

    [Fact]
    public void Drive_root_workspace_is_rejected()
    {
        using var testDirectory = new TestDirectory();
        var video = testDirectory.CreateFile("truth.mp4");
        var driveRoot = Path.GetPathRoot(testDirectory.Path)!;
        var draft = CreateDraft(driveRoot, video);

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Contains(issues, issue => issue.Code == "Workspace.Invalid");
    }

    [Fact]
    public void Non_mp4_selection_is_rejected()
    {
        using var testDirectory = new TestDirectory();
        var video = testDirectory.CreateFile("truth.mov");
        var draft = CreateDraft(testDirectory.Path, video);

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Contains(issues, issue => issue.Code == "TrainingVideo.Mp4Required");
    }

    [Fact]
    public void Duplicate_path_across_training_conditions_is_rejected()
    {
        using var testDirectory = new TestDirectory();
        var video = testDirectory.CreateFile("sample.mp4");
        var selections = new[]
        {
            new LocalTrainingVideoSelection(video, "first", TrainingCondition.VerifiedSincereTruth),
            new LocalTrainingVideoSelection(video, "second", TrainingCondition.VerifiedIntentionalDeception),
        };
        var draft = new ProfileDraft("Subject A", testDirectory.Path, null, selections);

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Contains(issues, issue => issue.Code == "TrainingVideo.Duplicate");
    }

    [Fact]
    public void Training_input_or_package_is_required()
    {
        using var testDirectory = new TestDirectory();
        var draft = new ProfileDraft("Subject A", testDirectory.Path, null, []);

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Contains(issues, issue => issue.Code == "Input.Required");
    }

    [Fact]
    public void Recording_date_label_is_preserved_verbatim()
    {
        const string label = "  recorded sometime after rehearsal B  ";
        var selection = new LocalTrainingVideoSelection(
            @"C:\inputs\truth.mp4",
            label,
            TrainingCondition.VerifiedSincereTruth);

        Assert.Equal(label, selection.RecordingDateLabel);
    }

    [Fact]
    public void Valid_local_mp4_draft_has_no_validation_issues_and_remains_draft()
    {
        using var testDirectory = new TestDirectory();
        var video = testDirectory.CreateFile("truth.mp4");
        var draft = CreateDraft(testDirectory.Path, video);

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Empty(issues);
        Assert.Equal(ProfileReadiness.Draft, draft.Readiness);
    }

    [Fact]
    public void Missing_archived_video_does_not_block_metadata_edit()
    {
        using var testDirectory = new TestDirectory();
        var selection = new LocalTrainingVideoSelection(
            Path.Combine(testDirectory.Path, "removed.mp4"),
            "old recording",
            TrainingCondition.VerifiedSincereTruth,
            IsArchived: true);
        var draft = new ProfileDraft("Subject A", testDirectory.Path, null, [selection]);

        var issues = ProfileDraftValidator.Validate(draft, requireActiveInput: false);

        Assert.Empty(issues);
    }

    [Fact]
    public void Persisted_missing_active_video_can_be_skipped_during_metadata_edit()
    {
        using var testDirectory = new TestDirectory();
        var selection = new LocalTrainingVideoSelection(
            Path.Combine(testDirectory.Path, "moved.mp4"),
            "old recording",
            TrainingCondition.VerifiedSincereTruth);
        var draft = new ProfileDraft("Subject A", testDirectory.Path, null, [selection]);

        var issues = ProfileDraftValidator.Validate(
            draft,
            requireActiveInput: false,
            validateSourceExistence: false);

        Assert.Empty(issues);
    }

    [Fact]
    public void Recording_dependency_group_names_are_unique_ignoring_case()
    {
        using var testDirectory = new TestDirectory();
        var video = testDirectory.CreateFile("truth.mp4");
        var draft = new ProfileDraft(
            "Subject A",
            testDirectory.Path,
            null,
            [new LocalTrainingVideoSelection(video, "one", TrainingCondition.VerifiedSincereTruth)],
            recordingDependencyGroups:
            [
                new(Guid.NewGuid(), "Session Å"),
                new(Guid.NewGuid(), "session å"),
            ]);

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Contains(
            issues,
            issue => issue.Code == "RecordingDependencyGroup.DuplicateName");
    }

    [Fact]
    public void Unassigned_is_reserved_for_the_null_group_selection()
    {
        using var testDirectory = new TestDirectory();
        var video = testDirectory.CreateFile("truth.mp4");
        var draft = new ProfileDraft(
            "Subject A",
            testDirectory.Path,
            null,
            [new LocalTrainingVideoSelection(video, "one", TrainingCondition.VerifiedSincereTruth)],
            recordingDependencyGroups: [new(Guid.NewGuid(), "uNaSsIgNeD")]);

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Contains(
            issues,
            issue => issue.Code == "RecordingDependencyGroup.NameReserved");
    }

    [Fact]
    public void Training_video_group_must_belong_to_the_profile_draft()
    {
        using var testDirectory = new TestDirectory();
        var video = testDirectory.CreateFile("truth.mp4");
        var draft = new ProfileDraft(
            "Subject A",
            testDirectory.Path,
            null,
            [new LocalTrainingVideoSelection(
                video,
                "one",
                TrainingCondition.VerifiedSincereTruth,
                RecordingDependencyGroupId: Guid.NewGuid())]);

        var issues = ProfileDraftValidator.Validate(draft);

        Assert.Contains(
            issues,
            issue => issue.Code == "TrainingVideo.RecordingDependencyGroupUnknown");
    }

    private static ProfileDraft CreateDraft(
        string workspaceRoot,
        string video,
        string displayName = "Subject A") =>
        new(
            displayName,
            workspaceRoot,
            null,
            [new LocalTrainingVideoSelection(video, "2026-01", TrainingCondition.VerifiedSincereTruth)]);
}
