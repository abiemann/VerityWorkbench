using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VerityWorkbench.App.ViewModels;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Core.Workspaces;
using VerityWorkbench.Data.Profiles;
using VerityWorkbench.Media;
using Windows.System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VerityWorkbench.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan PendingLocatorRecoveryAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProcessingJobRecoveryAge = TimeSpan.FromMinutes(10);

    private readonly ObservableCollection<ProfileSummaryViewModel> _profiles = [];
    private readonly ObservableCollection<TrainingVideoItemViewModel> _truthfulVideos = [];
    private readonly ObservableCollection<TrainingVideoItemViewModel> _deceptionVideos = [];
    private readonly LocalMediaStagingService _localMediaStagingService = new();
    private readonly SqliteProfileCatalog _profileCatalog;
    private EditorMode _editorMode;
    private StoredProfile? _editingProfile;
    private CancellationTokenSource? _activeProcessingCancellation;
    private Guid? _activeProcessingProfileId;
    private bool _processingCanBeCancelled;
    private bool _profileStorageReady;
    private int _recoveredProcessingJobCount;
    private int _reconciledPromotionCount;
    private int _promotionRecoveryWarningCount;
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

        if (selected.Readiness == ProfileReadiness.IngestingMedia.ToString())
        {
            StatusText.Text = "This profile has an active media-ingest job and cannot be edited yet.";
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

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateProfileActionButtons();

    private async void RefreshProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcessingCancellation is not null)
        {
            StatusText.Text = "Wait for the active media-ingest job to finish or cancel it before refreshing.";
            return;
        }

        var selectedId = (ProfilesList.SelectedItem as ProfileSummaryViewModel)?.Id;
        RefreshProfilesButton.IsEnabled = false;
        AddProfileButton.IsEnabled = false;
        EditProfileButton.IsEnabled = false;
        ProcessDataButton.IsEnabled = false;
        StatusText.Text = "Refreshing profiles and reconciling stale ingest jobs…";
        try
        {
            await ReloadProfilesAsync(selectedId);
            StatusText.Text = BuildLoadedProfilesStatus();
            if (_profiles.Any(profile => profile.Readiness == ProfileReadiness.IngestingMedia.ToString()))
            {
                StatusText.Text += " A fresh job may still be active in another app window; a job with no heartbeat for ten minutes is recovered on Refresh.";
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
            StatusText.Text = "Profiles could not be refreshed: " + exception.Message;
        }
        finally
        {
            AddProfileButton.IsEnabled = _profileStorageReady;
            EditProfileButton.IsEnabled = _profileStorageReady;
            UpdateProfileActionButtons();
        }
    }

    private void CancelProcessing_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcessingCancellation is null)
        {
            StatusText.Text = "No media-ingest job is running in this app window.";
            return;
        }

        if (!_processingCanBeCancelled)
        {
            StatusText.Text = "Media registration is already committed and the workspace state is being finalized.";
            return;
        }

        CancelProcessingButton.IsEnabled = false;
        StatusText.Text = "Cancelling media ingest and closing open files…";
        _activeProcessingCancellation.Cancel();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_processingCanBeCancelled)
        {
            _activeProcessingCancellation?.Cancel();
        }
    }

    private async void ProcessData_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcessingCancellation is not null)
        {
            StatusText.Text = "A media-ingest job is already running in this app window.";
            return;
        }

        if (ProfilesList.SelectedItem is not ProfileSummaryViewModel selected)
        {
            StatusText.Text = "Select a saved profile before choosing Process Data.";
            return;
        }

        SqliteProfileStore? profileStore = null;
        ProfileWorkspaceLayout? layout = null;
        Guid jobId = Guid.Empty;
        var jobStarted = false;
        var databaseCompletionCommitted = false;
        var mediaIntegrityFailure = false;
        var promotedAssets = new List<PromotedLocalMediaAsset>();
        CancellationTokenSource? heartbeatStop = null;
        Task<Exception?>? heartbeatTask = null;
        Exception? heartbeatFailure = null;
        var heartbeatAwaited = false;

        async Task<Exception?> StopHeartbeatAsync()
        {
            if (heartbeatAwaited)
            {
                return heartbeatFailure;
            }

            heartbeatAwaited = true;
            if (heartbeatStop is null || heartbeatTask is null)
            {
                return null;
            }

            heartbeatStop.Cancel();
            heartbeatFailure = await heartbeatTask;
            return heartbeatFailure;
        }

        var processingCancellation = new CancellationTokenSource();
        _activeProcessingCancellation = processingCancellation;
        _activeProcessingProfileId = selected.Id;
        _processingCanBeCancelled = true;
        SetProcessingUiState(isProcessing: true);
        selected.SetLiveStatus("Preparing local media ingest…");
        StatusText.Text = "Checking the selected profile and local MP4 sources…";

        try
        {
            profileStore = CreateProfileStore(selected.WorkspaceRoot);
            var profile = await profileStore.GetByIdAsync(selected.Id, processingCancellation.Token)
                ?? throw new KeyNotFoundException("The selected profile no longer exists.");
            if (profile.Readiness == ProfileReadiness.IngestingMedia.ToString())
            {
                throw new ProfileProcessingActiveException(profile.Id);
            }

            layout = ProfileWorkspaceLayout.Create(profile.WorkspaceRoot, profile.DownloadStagingRoot);
            var linkedAssetIds = profile.TrainingVideos
                .Where(video => !video.IsArchived && video.MediaAssetId is not null)
                .Select(video => video.MediaAssetId!.Value)
                .Distinct()
                .ToArray();
            if (linkedAssetIds.Length > 0)
            {
                selected.SetLiveStatus("Verifying registered workspace media…");
                StatusText.Text = "Verifying the length and SHA-256 of registered workspace media…";
                var registeredAssets = (await profileStore.GetMediaAssetsAsync(
                        profile.Id,
                        processingCancellation.Token))
                    .ToDictionary(asset => asset.Id);
                foreach (var linkedAssetId in linkedAssetIds)
                {
                    processingCancellation.Token.ThrowIfCancellationRequested();
                    if (!registeredAssets.TryGetValue(linkedAssetId, out var registeredAsset))
                    {
                        mediaIntegrityFailure = true;
                        throw new InvalidDataException(
                            "A training selection references media metadata that is no longer available.");
                    }

                    try
                    {
                        await _localMediaStagingService.VerifyExistingAssetAsync(
                            layout,
                            registeredAsset.Id,
                            registeredAsset.WorkspaceRelativePath,
                            registeredAsset.Sha256,
                            registeredAsset.ByteLength,
                            processingCancellation.Token);
                    }
                    catch (Exception exception) when (
                        exception is FileNotFoundException or InvalidDataException)
                    {
                        mediaIntegrityFailure = true;
                        throw new InvalidDataException(
                            "A registered workspace media copy is missing or no longer matches its recorded integrity metadata. "
                            + "The app left all files and metadata unchanged; automatic repair is not implemented yet.",
                            exception);
                    }
                }
            }

            var pendingVideos = profile.TrainingVideos
                .Where(video => !video.IsArchived && video.MediaAssetId is null)
                .OrderBy(video => video.SortOrder)
                .ToArray();
            if (pendingVideos.Length == 0)
            {
                selected.SetLiveStatus(null);
                StatusText.Text = "All active workspace copies passed length and SHA-256 verification. FFmpeg validation is the next implementation slice.";
                return;
            }

            var byteLengths = new Dictionary<Guid, long>();
            long totalBytes = 0;
            foreach (var video in pendingVideos)
            {
                processingCancellation.Token.ThrowIfCancellationRequested();
                if (!File.Exists(video.FilePath))
                {
                    throw new FileNotFoundException(
                        $"A selected source MP4 is missing: {video.FilePath}",
                        video.FilePath);
                }

                var length = new FileInfo(video.FilePath).Length;
                if (length <= 0)
                {
                    throw new InvalidDataException(
                        $"A selected source MP4 is empty: {video.FilePath}");
                }

                byteLengths.Add(video.Id, length);
                totalBytes = checked(totalBytes + length);
            }

            jobId = Guid.NewGuid();
            var startedAtUtc = DateTimeOffset.UtcNow;
            if (startedAtUtc <= profile.UpdatedAtUtc)
            {
                startedAtUtc = profile.UpdatedAtUtc.AddTicks(1);
            }

            var relativeJobPath = LocalMediaStagingService.BuildJobRelativePath(jobId, startedAtUtc);
            await profileStore.StartLocalMediaIngestJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                jobId,
                relativeJobPath,
                pendingVideos.Length,
                totalBytes,
                startedAtUtc,
                processingCancellation.Token);
            jobStarted = true;

            if (!await profileStore.UpdateProcessingJobProgressAsync(
                    jobId,
                    ProcessingJobState.Running,
                    0,
                    0,
                    DateTimeOffset.UtcNow,
                    processingCancellation.Token))
            {
                throw new InvalidOperationException("The media-ingest job stopped before file copying began.");
            }

            long latestCompletedBytes = 0;
            var latestCompletedItems = 0;
            var stagingCompleted = 0;
            var prefixBytes = new Dictionary<Guid, long>();
            long prefix = 0;
            foreach (var video in pendingVideos)
            {
                prefixBytes.Add(video.Id, prefix);
                prefix = checked(prefix + byteLengths[video.Id]);
            }

            var progress = new InlineProgress<LocalMediaStagingProgress>(update =>
            {
                var aggregateBytes = checked(prefixBytes[update.TrainingVideoId] + update.BytesCopied);
                var aggregateItems = update.ItemNumber - 1;
                Interlocked.Exchange(ref latestCompletedBytes, aggregateBytes);
                Interlocked.Exchange(ref latestCompletedItems, aggregateItems);

                var percentage = totalBytes == 0
                    ? 0
                    : Math.Clamp(aggregateBytes * 100d / totalBytes, 0d, 100d);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_activeProcessingProfileId != selected.Id
                        || Volatile.Read(ref stagingCompleted) != 0)
                    {
                        return;
                    }

                    var liveStatus = string.Create(
                        CultureInfo.CurrentCulture,
                        $"Copying media {percentage:0}% · {aggregateItems}/{pendingVideos.Length} complete");
                    selected.SetLiveStatus(liveStatus);
                    StatusText.Text = liveStatus + ". Source files remain unchanged.";
                });
            });

            heartbeatStop = new CancellationTokenSource();
            heartbeatTask = RunProcessingHeartbeatAsync(
                profileStore,
                jobId,
                () => (
                    Volatile.Read(ref latestCompletedItems),
                    Volatile.Read(ref latestCompletedBytes)),
                processingCancellation,
                heartbeatStop.Token);

            var stagingResult = await _localMediaStagingService.StageAsync(
                layout,
                jobId,
                startedAtUtc,
                pendingVideos
                    .Select(video => new LocalMediaStageRequest(video.Id, video.FilePath))
                    .ToArray(),
                progress,
                processingCancellation.Token);
            processingCancellation.Token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref latestCompletedBytes, totalBytes);
            Interlocked.Exchange(ref latestCompletedItems, pendingVideos.Length);
            Interlocked.Exchange(ref stagingCompleted, 1);
            selected.SetLiveStatus($"Finalizing media · {pendingVideos.Length}/{pendingVideos.Length} copied");
            StatusText.Text = "All selected files were copied and verified. Finalizing workspace assets…";

            var pendingById = pendingVideos.ToDictionary(video => video.Id);
            var existingAssets = await profileStore.GetMediaAssetsAsync(
                profile.Id,
                processingCancellation.Token);
            var existingAssetsByHash = existingAssets.ToDictionary(
                asset => asset.Sha256,
                StringComparer.Ordinal);
            var existingConditionsByAsset = profile.TrainingVideos
                .Where(video => video.MediaAssetId is not null)
                .GroupBy(video => video.MediaAssetId!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(video => video.Condition).Distinct().ToArray());

            var registrations = new List<MediaAssetRegistration>(stagingResult.Items.Count);
            foreach (var hashGroup in stagingResult.Items.GroupBy(item => item.Sha256, StringComparer.Ordinal))
            {
                processingCancellation.Token.ThrowIfCancellationRequested();
                var groupItems = hashGroup.ToArray();
                var conditions = groupItems
                    .Select(item => pendingById[item.TrainingVideoId].Condition)
                    .Distinct()
                    .ToArray();
                if (conditions.Length > 1)
                {
                    throw new MediaAssetConditionConflictException(
                        hashGroup.Key,
                        conditions[0],
                        conditions[1]);
                }

                if (groupItems.Any(item => item.ByteLength != groupItems[0].ByteLength))
                {
                    throw new InvalidDataException(
                        "Staged files with the same SHA-256 hash have inconsistent lengths.");
                }

                Guid assetId;
                string workspaceRelativePath;
                if (existingAssetsByHash.TryGetValue(hashGroup.Key, out var existingAsset))
                {
                    await _localMediaStagingService.VerifyExistingAssetAsync(
                        layout,
                        existingAsset.Id,
                        existingAsset.WorkspaceRelativePath,
                        existingAsset.Sha256,
                        existingAsset.ByteLength,
                        processingCancellation.Token);
                    if (existingConditionsByAsset.TryGetValue(existingAsset.Id, out var existingConditions)
                        && existingConditions.Any(condition => condition != conditions[0]))
                    {
                        throw new MediaAssetConditionConflictException(
                            hashGroup.Key,
                            existingConditions.First(condition => condition != conditions[0]),
                            conditions[0]);
                    }

                    assetId = existingAsset.Id;
                    workspaceRelativePath = existingAsset.WorkspaceRelativePath;
                }
                else
                {
                    var stagedItem = groupItems[0];
                    var video = pendingById[stagedItem.TrainingVideoId];
                    assetId = Guid.NewGuid();
                    var promoted = await _localMediaStagingService.PromoteAsync(
                        layout,
                        stagedItem,
                        video.RecordingDateLabel,
                        stagedItem.SourceFileName,
                        assetId,
                        processingCancellation.Token);
                    promotedAssets.Add(promoted);
                    workspaceRelativePath = promoted.WorkspaceRelativeOriginalPath;
                }

                registrations.AddRange(groupItems.Select(item => new MediaAssetRegistration(
                    item.TrainingVideoId,
                    assetId,
                    item.Sha256,
                    workspaceRelativePath,
                    item.ByteLength)));
            }

            var heartbeatError = await StopHeartbeatAsync();
            if (heartbeatError is not null)
            {
                throw new IOException("Media-ingest progress could not be persisted.", heartbeatError);
            }

            if (!await profileStore.UpdateProcessingJobProgressAsync(
                    jobId,
                    ProcessingJobState.Running,
                    pendingVideos.Length,
                    totalBytes,
                    DateTimeOffset.UtcNow,
                    processingCancellation.Token))
            {
                throw new InvalidOperationException("The media-ingest job stopped before completion.");
            }

            var completedAssets = await profileStore.CompleteLocalMediaIngestJobAsync(
                jobId,
                registrations,
                DateTimeOffset.UtcNow,
                processingCancellation.Token);
            databaseCompletionCommitted = true;
            _processingCanBeCancelled = false;
            CancelProcessingButton.IsEnabled = false;
            selected.SetLiveStatus("Media registered · finalizing workspace state…");
            StatusText.Text = "Media registration committed. Finalizing workspace state…";

            var completedAssetsByHash = completedAssets.ToDictionary(
                asset => asset.Sha256,
                StringComparer.Ordinal);
            var promotionCleanupWarning = false;
            foreach (var promoted in promotedAssets.AsEnumerable().Reverse())
            {
                if (completedAssetsByHash[promoted.Sha256].Id == promoted.AssetId)
                {
                    try
                    {
                        _localMediaStagingService.CommitPromotion(layout, promoted);
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        promotionCleanupWarning = true;
                    }

                    continue;
                }

                try
                {
                    _localMediaStagingService.RollbackPromotion(layout, promoted);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    promotionCleanupWarning = true;
                }
            }

            await ReloadProfilesAsync(profile.Id);
            StatusText.Text = promotionCleanupWarning
                ? "Media was ingested and is awaiting FFmpeg validation, but promotion-journal cleanup needs inspection in the Processing and Media folders."
                : "Media ingested successfully. The app-managed copies are awaiting FFmpeg validation; no analysis or scoring was performed.";
        }
        catch (OperationCanceledException)
        {
            var heartbeatError = await StopHeartbeatAsync();
            var terminalState = heartbeatError is null
                ? ProcessingJobState.Cancelled
                : ProcessingJobState.Failed;
            var rollbackWarning = !databaseCompletionCommitted
                && layout is not null
                && !RollbackPromotions(layout, promotedAssets);
            var terminalStateRecorded = true;
            if (jobStarted && !databaseCompletionCommitted && profileStore is not null)
            {
                terminalStateRecorded = await TryTerminateProcessingJobAsync(
                    profileStore,
                    jobId,
                    terminalState,
                    terminalState == ProcessingJobState.Failed
                        ? "Media ingest stopped because progress persistence failed."
                        : null);
            }

            var refreshWarning = jobStarted
                ? await TryReloadProfilesAfterProcessingAsync(selected.Id)
                : null;
            StatusText.Text = terminalState == ProcessingJobState.Cancelled
                ? jobStarted
                    ? "Media ingest cancelled. Open files were closed and no partial copy was promoted. The bounded Processing job folder was retained."
                    : "Media verification cancelled. Open files were closed and no processing job was created."
                : "Media ingest failed because its progress could not be persisted. No partial copy was accepted.";
            if (rollbackWarning)
            {
                StatusText.Text += " A promoted folder could not be returned to the Processing job; inspect the workspace.";
            }

            if (!terminalStateRecorded)
            {
                StatusText.Text += " The terminal job status could not be saved; use Refresh after the ten-minute recovery grace period.";
            }

            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed: " + refreshWarning;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or ArithmeticException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            await StopHeartbeatAsync();
            var rollbackWarning = !databaseCompletionCommitted
                && layout is not null
                && !RollbackPromotions(layout, promotedAssets);
            var terminalStateRecorded = true;
            if (jobStarted && !databaseCompletionCommitted && profileStore is not null)
            {
                terminalStateRecorded = await TryTerminateProcessingJobAsync(
                    profileStore,
                    jobId,
                    ProcessingJobState.Failed,
                    "Media ingest failed before completion.");
            }

            var refreshWarning = jobStarted
                ? await TryReloadProfilesAfterProcessingAsync(selected.Id)
                : null;
            StatusText.Text = mediaIntegrityFailure
                ? "Workspace media verification failed: " + exception.Message
                : exception is MediaAssetConditionConflictException
                ? "The same media content appears in both training conditions. Resolve the conflicting labels before processing again."
                : databaseCompletionCommitted
                    ? "Media ingest completed, but the profile list could not be refreshed: " + exception.Message
                    : "Media ingest failed; no partial copy was accepted: " + exception.Message;
            if (rollbackWarning)
            {
                StatusText.Text += " A promoted folder could not be returned to the Processing job; inspect the workspace.";
            }

            if (!terminalStateRecorded)
            {
                StatusText.Text += " The failed job status could not be saved; use Refresh after the ten-minute recovery grace period.";
            }

            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed: " + refreshWarning;
            }
        }
        finally
        {
            await StopHeartbeatAsync();
            heartbeatStop?.Dispose();
            processingCancellation.Dispose();
            if (_activeProcessingProfileId == selected.Id)
            {
                _activeProcessingCancellation = null;
                _activeProcessingProfileId = null;
            }

            _processingCanBeCancelled = false;

            selected.SetLiveStatus(mediaIntegrityFailure ? "Workspace media needs repair" : null);
            SetProcessingUiState(isProcessing: false);
        }
    }

    private static async Task<Exception?> RunProcessingHeartbeatAsync(
        SqliteProfileStore profileStore,
        Guid jobId,
        Func<(int CompletedItems, long CompletedBytes)> readProgress,
        CancellationTokenSource processingCancellation,
        CancellationToken stopToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(stopToken))
            {
                var progress = readProgress();
                if (!await profileStore.UpdateProcessingJobProgressAsync(
                        jobId,
                        ProcessingJobState.Running,
                        progress.CompletedItems,
                        progress.CompletedBytes,
                        DateTimeOffset.UtcNow,
                        stopToken))
                {
                    throw new InvalidOperationException(
                        "The media-ingest job is no longer active in profile storage.");
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            processingCancellation.Cancel();
            return exception;
        }
    }

    private bool RollbackPromotions(
        ProfileWorkspaceLayout layout,
        IEnumerable<PromotedLocalMediaAsset> promotedAssets)
    {
        var allRolledBack = true;
        foreach (var promoted in promotedAssets.Reverse())
        {
            try
            {
                _localMediaStagingService.RollbackPromotion(layout, promoted);
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                allRolledBack = false;
            }
        }

        return allRolledBack;
    }

    private static async Task<bool> TryTerminateProcessingJobAsync(
        SqliteProfileStore profileStore,
        Guid jobId,
        ProcessingJobState terminalState,
        string? error)
    {
        try
        {
            return await profileStore.TerminateProcessingJobAsync(
                jobId,
                terminalState,
                error,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            // The stale-job recovery path will reconcile a terminal-state write failure.
            return false;
        }
    }

    private async Task<string?> TryReloadProfilesAfterProcessingAsync(Guid profileId)
    {
        try
        {
            await ReloadProfilesAsync(profileId);
            return null;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            return exception.Message;
        }
    }

    private void SetProcessingUiState(bool isProcessing)
    {
        AddProfileButton.IsEnabled = _profileStorageReady && !isProcessing;
        EditProfileButton.IsEnabled = _profileStorageReady && !isProcessing;
        RefreshProfilesButton.IsEnabled = _profileStorageReady && !isProcessing;
        ProfilesList.IsEnabled = !isProcessing;
        UpdateProfileActionButtons();
    }

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

        if (!video.CanRemove)
        {
            StatusText.Text = "Ingested media cannot be removed as an unprocessed selection. Archive it to exclude it from future work.";
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
            var storedVideos = BuildStoredVideos();
            var activeStoredVideos = storedVideos.Where(video => !video.IsArchived).ToArray();
            var readiness = activeStoredVideos.Length > 0
                && activeStoredVideos.All(video => video.MediaAssetId is not null)
                    ? ProfileReadiness.MediaIngestedAwaitingProbe
                    : ProfileReadiness.Draft;
            var storedProfile = new StoredProfile(
                editingProfile?.Id ?? Guid.NewGuid(),
                draft.DisplayName,
                layout.WorkspaceRoot,
                string.IsNullOrWhiteSpace(draft.DownloadStagingRoot)
                    ? null
                    : layout.DownloadStagingRoot,
                readiness.ToString(),
                editingProfile?.CreatedAtUtc ?? now,
                now,
                storedVideos);

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
                ? storedProfile.Readiness == ProfileReadiness.MediaIngestedAwaitingProbe.ToString()
                    ? "Profile changes saved. Every active media selection remains registered and awaits validation."
                    : "Profile changes saved. New or changed active selections await media ingest."
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
            _profileStorageReady = true;
            UpdateProfileActionButtons();
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
        _recoveredProcessingJobCount = 0;
        _reconciledPromotionCount = 0;
        _promotionRecoveryWarningCount = 0;
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
                var recoveredAtUtc = DateTimeOffset.UtcNow;
                var staleBeforeUtc = recoveredAtUtc - ProcessingJobRecoveryAge;
                var processingJobs = await profileStore.GetProcessingJobsAsync(locator.ProfileId);
                var eligibleJournalJobs = processingJobs
                    .Where(job =>
                        job.State is not ProcessingJobState.Queued and not ProcessingJobState.Running
                        || job.UpdatedAtUtc < staleBeforeUtc)
                    .Select(job => job.Id)
                    .ToHashSet();
                var committedAssets = await profileStore.GetMediaAssetsAsync(locator.ProfileId);
                var reconciliation = await _localMediaStagingService.ReconcilePendingPromotionsAsync(
                    layout,
                    committedAssets.ToDictionary(
                        asset => asset.Id,
                        asset => asset.WorkspaceRelativePath),
                    eligibleJournalJobs);
                _reconciledPromotionCount += reconciliation.CompletedCommittedPromotions
                    + reconciliation.RolledBackUncommittedPromotions
                    + reconciliation.ClearedPreparedPromotions;
                _promotionRecoveryWarningCount += reconciliation.WarningCount;
                _recoveredProcessingJobCount += await profileStore.RecoverInterruptedJobsAsync(
                    staleBeforeUtc,
                    recoveredAtUtc);
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
                profile.TrainingVideos.Count(video => video.IsArchived),
                activeVideos.Count(video => video.MediaAssetId is null),
                profile.Readiness);
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
        UpdateProfileActionButtons();

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
        var recoveryStatus = _recoveredProcessingJobCount == 0
            ? string.Empty
            : $"; {_recoveredProcessingJobCount} stale media-ingest job(s) marked interrupted";
        var promotionStatus = _reconciledPromotionCount == 0
            ? string.Empty
            : $"; {_reconciledPromotionCount} interrupted media promotion(s) reconciled";
        var promotionWarning = _promotionRecoveryWarningCount == 0
            ? string.Empty
            : $"; {_promotionRecoveryWarningCount} media promotion journal(s) need manual inspection";
        return loadedStatus
            + unavailableStatus
            + recoveryStatus
            + promotionStatus
            + promotionWarning
            + ". No analysis or scoring is implemented.";
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
                isPersisted: true,
                storedVideo.MediaAssetId));
        }
    }

    private void ConfigureEditorForAdd()
    {
        ProfileFormTitle.Text = "Add Profile";
        ProfileFormDescription.Text =
            "Create a persistent draft and its inspectable local workspace. Saving does not copy media; use Process Data from the main view afterward.";
        SaveDraftButton.Content = "Save draft";
        ChooseWorkspaceButton.IsEnabled = true;
        ChooseDownloadRootButton.IsEnabled = true;
        UseDefaultDownloadRootButton.IsEnabled = true;
    }

    private void ConfigureEditorForEdit()
    {
        ProfileFormTitle.Text = "Edit Profile";
        ProfileFormDescription.Text =
            "Edit metadata and training eligibility. Saving does not copy media; use Process Data from the main view for new active selections. Workspace relocation is deferred.";
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
                sortOrder++,
                video.MediaAssetId))
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
        UpdateProfileActionButtons();
    }

    private void UpdateProfileActionButtons()
    {
        var selected = ProfilesList.SelectedItem as ProfileSummaryViewModel;
        var processingInThisWindow = _activeProcessingCancellation is not null;
        ProcessDataButton.IsEnabled = _profileStorageReady
            && !processingInThisWindow
            && selected?.CanStartIngest == true;
        CancelProcessingButton.IsEnabled = processingInThisWindow && _processingCanBeCancelled;
        RefreshProfilesButton.IsEnabled = _profileStorageReady && !processingInThisWindow;
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Action<T> _report = report ?? throw new ArgumentNullException(nameof(report));

        public void Report(T value) => _report(value);
    }
}
