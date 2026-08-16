using System.Collections.ObjectModel;
using System.Data.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VerityWorkbench.App.ViewModels;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Core.Workspaces;
using VerityWorkbench.Data.Profiles;
using Windows.System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VerityWorkbench.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan PendingLocatorRecoveryAge = TimeSpan.FromMinutes(10);

    private readonly ObservableCollection<ProfileSummaryViewModel> _profiles = [];
    private readonly ObservableCollection<TrainingVideoItemViewModel> _truthfulVideos = [];
    private readonly ObservableCollection<TrainingVideoItemViewModel> _deceptionVideos = [];
    private readonly SqliteProfileCatalog _profileCatalog;
    private EditorMode _editorMode;
    private StoredProfile? _editingProfile;
    private int _unavailableProfileCount;

    public MainWindow()
    {
        InitializeComponent();
        ProfilesList.ItemsSource = _profiles;
        TruthfulVideosList.ItemsSource = _truthfulVideos;
        DeceptionVideosList.ItemsSource = _deceptionVideos;

        var localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var catalogPath = Path.Combine(localDataRoot, "VerityWorkbench", "profile-catalog.sqlite");
        _profileCatalog = new SqliteProfileCatalog(catalogPath);
        AddProfileButton.IsEnabled = false;
        EditProfileButton.IsEnabled = false;
        _ = InitializeProfileStorageAsync();
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        ResetDraftForm();
        _editorMode = EditorMode.Add;
        _editingProfile = null;
        ConfigureEditorForAdd();
        HideValidation();
        MainView.Visibility = Visibility.Collapsed;
        AddProfileView.Visibility = Visibility.Visible;
        StatusText.Text = "Adding a draft profile. Nothing is written until Save draft is selected.";
    }

    private async void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProfileSummaryViewModel selected)
        {
            StatusText.Text = "Select a saved profile before choosing Edit Profile.";
            return;
        }

        AddProfileButton.IsEnabled = false;
        EditProfileButton.IsEnabled = false;
        StatusText.Text = "Opening the selected profile…";
        try
        {
            var profile = await CreateProfileStore(selected.WorkspaceRoot).GetByIdAsync(selected.Id);
            if (profile is null)
            {
                StatusText.Text = "The selected profile no longer exists. Refreshing the profile list.";
                await ReloadProfilesAsync();
                return;
            }

            ResetDraftForm();
            _editorMode = EditorMode.Edit;
            _editingProfile = profile;
            PopulateEditor(profile);
            ConfigureEditorForEdit();
            MainView.Visibility = Visibility.Collapsed;
            AddProfileView.Visibility = Visibility.Visible;
            StatusText.Text = "Editing a detached draft. Cancel discards every staged change.";
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            ResetDraftForm();
            _editingProfile = null;
            _editorMode = EditorMode.Add;
            StatusText.Text = "The profile could not be opened: " + exception.Message;
        }
        finally
        {
            AddProfileButton.IsEnabled = true;
            EditProfileButton.IsEnabled = true;
        }
    }

    private void QueryProfile_Click(object sender, RoutedEventArgs e) =>
        StatusText.Text = "Query Profile remains disabled by design until real processing and validation exist.";

    private async void ChooseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = await PickFolderAsync();
        if (selectedPath is not null)
        {
            WorkspaceRootBox.Text = selectedPath;
        }
    }

    private async void ChooseDownloadRoot_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = await PickFolderAsync();
        if (selectedPath is not null)
        {
            DownloadRootBox.Text = selectedPath;
        }
    }

    private void UseDefaultDownloadRoot_Click(object sender, RoutedEventArgs e) =>
        DownloadRootBox.Text = string.Empty;

    private async void AddTruthfulVideos_Click(object sender, RoutedEventArgs e) =>
        await AddVideosAsync(TrainingCondition.VerifiedSincereTruth);

    private async void AddDeceptionVideos_Click(object sender, RoutedEventArgs e) =>
        await AddVideosAsync(TrainingCondition.VerifiedIntentionalDeception);

    private void RemoveTrainingVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrainingVideoItemViewModel video })
        {
            return;
        }

        CollectionFor(video.Condition).Remove(video);
        StatusText.Text = video.IsPersisted
            ? "The saved, unprocessed selection is staged for removal. Cancel to retain it, or save changes to remove it."
            : "The new, unsaved selection was removed.";
    }

    private void ArchiveTrainingVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrainingVideoItemViewModel video } || !video.CanArchive)
        {
            return;
        }

        video.IsArchived = !video.IsArchived;
        StatusText.Text = video.IsArchived
            ? "The existing selection is staged for archiving. Save changes to persist it."
            : "The existing selection is staged for reactivation. Save changes to persist it.";
    }

    private void SortTruthfulVideos_Click(object sender, RoutedEventArgs e) =>
        SortVideos(_truthfulVideos);

    private void SortDeceptionVideos_Click(object sender, RoutedEventArgs e) =>
        SortVideos(_deceptionVideos);

    private void RecordingDateLabel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox textBox)
        {
            return;
        }

        if (textBox.DataContext is TrainingVideoItemViewModel video)
        {
            video.RecordingDateLabel = textBox.Text;
        }

        e.Handled = true;
        FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
        StatusText.Text = "Recording date label accepted. It remains display/sort metadata only.";
    }

    private void CancelAddProfile_Click(object sender, RoutedEventArgs e)
    {
        var cancelledMode = _editorMode;
        ResetDraftForm();
        ShowMainView();
        StatusText.Text = cancelledMode == EditorMode.Edit
            ? "Edit Profile cancelled. The saved profile was not changed."
            : "Add Profile cancelled. No profile or workspace folders were created.";
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        var operationMode = _editorMode;
        var editingProfile = _editingProfile;
        var selections = _truthfulVideos
            .Concat(_deceptionVideos)
            .Select(video => video.ToSelection())
            .ToArray();
        var draft = new ProfileDraft(
            ProfileNameBox.Text.Trim(),
            WorkspaceRootBox.Text,
            string.IsNullOrWhiteSpace(DownloadRootBox.Text) ? null : DownloadRootBox.Text,
            selections);

        var issues = ProfileDraftValidator
            .Validate(
                draft,
                requireActiveInput: operationMode == EditorMode.Add,
                validateSourceExistence: operationMode == EditorMode.Add)
            .ToList();
        if (operationMode == EditorMode.Edit)
        {
            foreach (var missingVideo in AllVideos().Where(video =>
                         !video.IsPersisted
                         && !video.IsArchived
                         && !File.Exists(video.FullPath)))
            {
                issues.Add(new(
                    "TrainingVideo.NotFound",
                    $"New training video not found: {missingVideo.FullPath}"));
            }
        }
        if (_profiles.Any(profile => string.Equals(
                profile.DisplayName,
                draft.DisplayName,
                StringComparison.OrdinalIgnoreCase)
            && profile.Id != editingProfile?.Id))
        {
            issues.Add(new(
                "ProfileName.Duplicate",
                "A profile with this display name already exists."));
        }

        if (WorkspaceOverlapsAnotherProfile(draft.WorkspaceRoot, editingProfile?.Id))
        {
            issues.Add(new(
                "Workspace.Overlap",
                "Choose a dedicated workspace that is not equal to, inside, or above another saved profile workspace."));
        }

        if (issues.Count > 0)
        {
            ShowValidation(issues.Select(issue => issue.Message));
            return;
        }

        var saveCommitted = false;
        AddProfileView.IsEnabled = false;
        try
        {
            var layout = ProfileWorkspaceLayout.Create(draft.WorkspaceRoot, draft.DownloadStagingRoot);
            var now = DateTimeOffset.UtcNow;
            var storedProfile = new StoredProfile(
                editingProfile?.Id ?? Guid.NewGuid(),
                draft.DisplayName,
                layout.WorkspaceRoot,
                string.IsNullOrWhiteSpace(draft.DownloadStagingRoot)
                    ? null
                    : layout.DownloadStagingRoot,
                ProfileReadiness.Draft.ToString(),
                editingProfile?.CreatedAtUtc ?? now,
                now,
                BuildStoredVideos());

            if (operationMode == EditorMode.Edit)
            {
                await CreateProfileStore(editingProfile!.WorkspaceRoot)
                    .UpdateAsync(storedProfile, editingProfile.UpdatedAtUtc);
                saveCommitted = true;
            }
            else
            {
                if (ProfileDatabaseArtifactsExist(layout))
                {
                    throw new InvalidOperationException(
                        "The selected workspace already contains profile-database files. Choose a new dedicated workspace.");
                }

                var locator = new StoredProfileLocator(
                    storedProfile.Id,
                    layout.WorkspaceRoot,
                    now);
                await _profileCatalog.AddPendingAsync(locator);
                try
                {
                    ProfileWorkspaceInitializer.Initialize(layout);
                    await new SqliteProfileStore(layout.ProfileDatabasePath).AddAsync(storedProfile);
                }
                catch
                {
                    await RemoveLocatorAfterFailedAddAsync(storedProfile.Id, layout);
                    throw;
                }

                saveCommitted = true;
                if (!await _profileCatalog.MarkReadyAsync(storedProfile.Id))
                {
                    throw new InvalidOperationException(
                        "The profile database was saved, but its catalog entry could not be marked ready.");
                }
            }

            await ReloadProfilesAsync(storedProfile.Id);
            ResetDraftForm();
            ShowMainView();
            StatusText.Text = operationMode == EditorMode.Edit
                ? "Profile changes saved. The profile remains Draft — not processed."
                : "Draft saved persistently. Workspace folders were created; media was not copied or processed.";
        }
        catch (Exception exception) when (
            saveCommitted
            && exception is (IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException))
        {
            ResetDraftForm();
            ShowMainView();
            StatusText.Text = "The profile was saved, but the profile list could not be refreshed. "
                + "Restart the app to reload it. Details: "
                + exception.Message;
        }
        catch (ProfileNameConflictException exception)
        {
            ShowValidation([exception.Message]);
        }
        catch (ProfileWorkspaceConflictException exception)
        {
            ShowValidation([exception.Message]);
        }
        catch (ProfileLocatorConflictException)
        {
            ShowValidation([
                "Choose a dedicated workspace that is not equal to, inside, or above another saved profile workspace.",
            ]);
        }
        catch (ProfileConcurrencyConflictException)
        {
            await ReturnToMainAfterExternalProfileChangeAsync(
                "This profile was changed in another app window. Your staged changes were not applied; reopen the refreshed profile and try again.");
        }
        catch (KeyNotFoundException)
        {
            await ReturnToMainAfterExternalProfileChangeAsync(
                "The profile no longer exists. The saved profile list was refreshed.");
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            ShowValidation(["The profile could not be saved: " + exception.Message]);
        }
        finally
        {
            AddProfileView.IsEnabled = true;
        }
    }

    private async Task InitializeProfileStorageAsync()
    {
        try
        {
            await _profileCatalog.InitializeAsync();
            await ReloadProfilesAsync();
            AddProfileButton.IsEnabled = true;
            EditProfileButton.IsEnabled = true;
            StatusText.Text = BuildLoadedProfilesStatus();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            StatusText.Text = "Profile storage is unavailable: " + exception.Message;
        }
    }

    private async Task ReloadProfilesAsync(Guid? profileToSelect = null)
    {
        var locators = await _profileCatalog.GetAllAsync();
        _profiles.Clear();
        _unavailableProfileCount = _profileCatalog.LastInvalidLocatorCount;

        ProfileSummaryViewModel? selection = null;
        foreach (var locator in locators)
        {
            StoredProfile? profile;
            try
            {
                var layout = ProfileWorkspaceLayout.Create(locator.WorkspaceRoot);
                if (!File.Exists(layout.ProfileDatabasePath))
                {
                    if (locator.State == ProfileLocatorState.Pending)
                    {
                        if (!IsStalePendingLocator(locator))
                        {
                            _unavailableProfileCount++;
                            continue;
                        }

                        DeleteProfileDatabaseArtifacts(layout);
                        await _profileCatalog.RemoveAsync(locator.ProfileId);
                        continue;
                    }

                    _unavailableProfileCount++;
                    continue;
                }

                var profileStore = CreateProfileStore(locator.WorkspaceRoot);
                profile = await profileStore.GetByIdAsync(locator.ProfileId);
                if (profile is null)
                {
                    if (locator.State == ProfileLocatorState.Pending
                        && (await profileStore.GetAllAsync()).Count == 0)
                    {
                        if (!IsStalePendingLocator(locator))
                        {
                            _unavailableProfileCount++;
                            continue;
                        }

                        DeleteProfileDatabaseArtifacts(layout);
                        await _profileCatalog.RemoveAsync(locator.ProfileId);
                        continue;
                    }

                    _unavailableProfileCount++;
                    continue;
                }

                if (!string.Equals(
                        profile.WorkspaceRoot,
                        locator.WorkspaceRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _unavailableProfileCount++;
                    continue;
                }

                if (locator.State == ProfileLocatorState.Pending
                    && !await _profileCatalog.MarkReadyAsync(locator.ProfileId))
                {
                    _unavailableProfileCount++;
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or DbException
                    or FormatException)
            {
                _unavailableProfileCount++;
                continue;
            }

            var activeVideos = profile.TrainingVideos.Where(video => !video.IsArchived).ToArray();
            var summary = new ProfileSummaryViewModel(
                profile.Id,
                profile.DisplayName,
                profile.WorkspaceRoot,
                activeVideos.Count(video => video.Condition == TrainingCondition.VerifiedSincereTruth),
                activeVideos.Count(video => video.Condition == TrainingCondition.VerifiedIntentionalDeception),
                profile.TrainingVideos.Count(video => video.IsArchived));
            _profiles.Add(summary);

            if (summary.Id == profileToSelect)
            {
                selection = summary;
            }
        }

        EmptyProfilesText.Visibility = _profiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyProfilesText.Text = _unavailableProfileCount == 0
            ? "No saved profiles yet."
            : "No profiles are currently available. See the status message for the unavailable count.";
        ProfilesList.SelectedItem = selection;

        if (profileToSelect.HasValue && selection is null)
        {
            throw new InvalidDataException(
                "The saved profile could not be reloaded from its workspace database.");
        }
    }

    private static SqliteProfileStore CreateProfileStore(string workspaceRoot)
    {
        var layout = ProfileWorkspaceLayout.Create(workspaceRoot);
        return new SqliteProfileStore(layout.ProfileDatabasePath, createIfMissing: false);
    }

    private async Task RemoveLocatorAfterFailedAddAsync(
        Guid profileId,
        ProfileWorkspaceLayout layout)
    {
        try
        {
            DeleteProfileDatabaseArtifacts(layout);
            await _profileCatalog.RemoveAsync(profileId);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException)
        {
            // Preserve the original save failure. A stale locator is surfaced as unavailable on restart.
        }
    }

    private async Task ReturnToMainAfterExternalProfileChangeAsync(string message)
    {
        ResetDraftForm();
        ShowMainView();
        try
        {
            await ReloadProfilesAsync();
            StatusText.Text = message;
        }
        catch (Exception refreshException) when (
            refreshException is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            StatusText.Text = message + " The profile list could not be refreshed: "
                + refreshException.Message;
        }
    }

    private static bool ProfileDatabaseArtifactsExist(ProfileWorkspaceLayout layout) =>
        ProfileDatabaseArtifactPaths(layout).Any(File.Exists);

    private static bool IsStalePendingLocator(StoredProfileLocator locator) =>
        locator.State == ProfileLocatorState.Pending
        && DateTimeOffset.UtcNow - locator.AddedAtUtc >= PendingLocatorRecoveryAge;

    private static void DeleteProfileDatabaseArtifacts(ProfileWorkspaceLayout layout)
    {
        foreach (var path in ProfileDatabaseArtifactPaths(layout))
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<string> ProfileDatabaseArtifactPaths(ProfileWorkspaceLayout layout)
    {
        yield return layout.ProfileDatabasePath;
        yield return layout.ProfileDatabasePath + "-journal";
        yield return layout.ProfileDatabasePath + "-wal";
        yield return layout.ProfileDatabasePath + "-shm";
    }

    private string BuildLoadedProfilesStatus()
    {
        var loadedStatus = _profiles.Count == 0
            ? "Ready. No saved profiles yet"
            : $"Loaded {_profiles.Count} saved profile(s)";
        var unavailableStatus = _unavailableProfileCount == 0
            ? string.Empty
            : $"; {_unavailableProfileCount} catalog entry or profile database is unavailable";
        return loadedStatus + unavailableStatus + ". No analysis or scoring is implemented.";
    }

    private void PopulateEditor(StoredProfile profile)
    {
        ProfileNameBox.Text = profile.DisplayName;
        WorkspaceRootBox.Text = profile.WorkspaceRoot;
        DownloadRootBox.Text = profile.DownloadStagingRoot ?? string.Empty;

        foreach (var storedVideo in profile.TrainingVideos.OrderBy(video => video.SortOrder))
        {
            CollectionFor(storedVideo.Condition).Add(new TrainingVideoItemViewModel(
                storedVideo.Id,
                storedVideo.FilePath,
                storedVideo.Condition,
                storedVideo.RecordingDateLabel,
                storedVideo.IsArchived,
                isPersisted: true));
        }
    }

    private void ConfigureEditorForAdd()
    {
        ProfileFormTitle.Text = "Add Profile";
        ProfileFormDescription.Text =
            "Create a persistent draft and its inspectable local workspace. No media is copied or processed yet.";
        SaveDraftButton.Content = "Save draft";
        ChooseWorkspaceButton.IsEnabled = true;
        ChooseDownloadRootButton.IsEnabled = true;
        UseDefaultDownloadRootButton.IsEnabled = true;
    }

    private void ConfigureEditorForEdit()
    {
        ProfileFormTitle.Text = "Edit Profile";
        ProfileFormDescription.Text =
            "Edit the saved draft metadata and training selections. Workspace relocation is deferred; no media is copied or processed.";
        SaveDraftButton.Content = "Save changes";
        ChooseWorkspaceButton.IsEnabled = false;
        ChooseDownloadRootButton.IsEnabled = false;
        UseDefaultDownloadRootButton.IsEnabled = false;
    }

    private IReadOnlyList<StoredTrainingVideo> BuildStoredVideos()
    {
        var sortOrder = 0;
        return _truthfulVideos
            .Concat(_deceptionVideos)
            .Select(video => new StoredTrainingVideo(
                video.Id,
                video.FullPath,
                video.RecordingDateLabel,
                video.Condition,
                video.IsArchived,
                sortOrder++))
            .ToArray();
    }

    private bool WorkspaceOverlapsAnotherProfile(string candidateRoot, Guid? excludedProfileId)
    {
        var validation = WorkspacePathPolicy.Validate(candidateRoot);
        if (!validation.IsValid)
        {
            return false;
        }

        return _profiles
            .Where(profile => profile.Id != excludedProfileId)
            .Any(profile => PathsOverlap(validation.NormalizedPath!, profile.WorkspaceRoot));
    }

    private static bool PathsOverlap(string first, string second)
    {
        var normalizedFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var normalizedSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        if (string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstWithSeparator = normalizedFirst + Path.DirectorySeparatorChar;
        var secondWithSeparator = normalizedSecond + Path.DirectorySeparatorChar;
        return firstWithSeparator.StartsWith(secondWithSeparator, StringComparison.OrdinalIgnoreCase)
            || secondWithSeparator.StartsWith(firstWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        return string.IsNullOrWhiteSpace(folder?.Path) ? null : folder.Path;
    }

    private async Task AddVideosAsync(TrainingCondition condition)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".mp4");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var files = await picker.PickMultipleFilesAsync();
        var skippedDuplicate = false;

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                continue;
            }

            var canonicalPath = Path.GetFullPath(file.Path);
            if (AllVideos().Any(video => string.Equals(
                    video.FullPath,
                    canonicalPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                skippedDuplicate = true;
                continue;
            }

            var item = new TrainingVideoItemViewModel(canonicalPath, condition);
            CollectionFor(condition).Add(item);
        }

        StatusText.Text = skippedDuplicate
            ? "Selected MP4 files were added; duplicate paths were skipped."
            : "Selected MP4 files were added to the draft. No files were copied.";
    }

    private IEnumerable<TrainingVideoItemViewModel> AllVideos() =>
        _truthfulVideos.Concat(_deceptionVideos);

    private ObservableCollection<TrainingVideoItemViewModel> CollectionFor(TrainingCondition condition) =>
        condition == TrainingCondition.VerifiedSincereTruth ? _truthfulVideos : _deceptionVideos;

    private static void SortVideos(ObservableCollection<TrainingVideoItemViewModel> videos)
    {
        var sorted = videos
            .OrderBy(video => video.RecordingDateLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(video => video.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        videos.Clear();
        foreach (var video in sorted)
        {
            videos.Add(video);
        }
    }

    private void ResetDraftForm()
    {
        ProfileNameBox.Text = string.Empty;
        WorkspaceRootBox.Text = string.Empty;
        DownloadRootBox.Text = string.Empty;
        _truthfulVideos.Clear();
        _deceptionVideos.Clear();
        HideValidation();
    }

    private void ShowMainView()
    {
        AddProfileView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        _editingProfile = null;
        _editorMode = EditorMode.Add;
    }

    private void ShowValidation(IEnumerable<string> messages)
    {
        ValidationText.Text = string.Join(Environment.NewLine, messages.Select(message => "• " + message));
        ValidationPanel.Visibility = Visibility.Visible;
        StatusText.Text = "The profile has errors and was not saved.";
    }

    private void HideValidation()
    {
        ValidationPanel.Visibility = Visibility.Collapsed;
        ValidationText.Text = string.Empty;
    }

    private enum EditorMode
    {
        Add,
        Edit,
    }
}
