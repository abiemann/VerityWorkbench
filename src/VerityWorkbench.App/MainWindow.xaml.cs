using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly ObservableCollection<RecordingDependencyGroupOptionViewModel> _recordingDependencyGroups = [];
    private readonly ObservableCollection<RecordingDependencyGroupOptionViewModel> _recordingDependencyGroupOptions = [];
    private readonly LocalMediaStagingService _localMediaStagingService = new();
    private readonly MediaValidationService _mediaValidationService = new();
    private readonly MediaPreprocessingService _mediaPreprocessingService = new();
    private readonly AudioPcmObservationService _audioPcmObservationService = new();
    private readonly SqliteProfileCatalog _profileCatalog;
    private EditorMode _editorMode;
    private StoredProfile? _editingProfile;
    private CancellationTokenSource? _activeProcessingCancellation;
    private Guid? _activeProcessingProfileId;
    private ProcessingJobKind? _activeProcessingKind;
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
        RecordingDependencyGroupsList.ItemsSource = _recordingDependencyGroups;
        ResetRecordingDependencyGroups();
        InitializePreparedMediaReview();
        InitializeProcessingHistory();

        var localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var catalogPath = Path.Combine(localDataRoot, "VerityWorkbench", "profile-catalog.sqlite");
        _profileCatalog = new SqliteProfileCatalog(catalogPath);
        AddProfileButton.IsEnabled = false;
        EditProfileButton.IsEnabled = false;
        _ = InitializeProfileStorageAsync();
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        ResetProcessingHistoryState(showMainView: false);
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

        if (IsProcessingReadiness(selected.Readiness))
        {
            StatusText.Text = "This profile has an active processing job and cannot be edited yet.";
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
            ResetProcessingHistoryState(showMainView: false);
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
        StatusText.Text = "Query Profile remains disabled by design until a trained, validated model and inference pipeline exist.";

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateProfileActionButtons();

    private async void RefreshProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcessingCancellation is not null)
        {
            StatusText.Text = "Wait for the active processing job to finish or cancel it before refreshing.";
            return;
        }

        var selectedId = (ProfilesList.SelectedItem as ProfileSummaryViewModel)?.Id;
        RefreshProfilesButton.IsEnabled = false;
        AddProfileButton.IsEnabled = false;
        EditProfileButton.IsEnabled = false;
        ProcessDataButton.IsEnabled = false;
        StatusText.Text = "Refreshing profiles and reconciling stale processing jobs…";
        try
        {
            await ReloadProfilesAsync(selectedId);
            StatusText.Text = BuildLoadedProfilesStatus();
            if (_profiles.Any(profile => IsProcessingReadiness(profile.Readiness)))
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
            StatusText.Text = "No processing job is running in this app window.";
            return;
        }

        if (!_processingCanBeCancelled)
        {
            StatusText.Text = "Processing results are already committed and the workspace state is being finalized.";
            return;
        }

        CancelProcessingButton.IsEnabled = false;
        StatusText.Text = _activeProcessingKind switch
        {
            ProcessingJobKind.MediaValidation =>
                "Cancelling media validation and closing FFmpeg files and processes…",
            ProcessingJobKind.MediaPreprocessing =>
                "Cancelling media preprocessing and closing FFmpeg files and processes…",
            ProcessingJobKind.AudioObservationExtraction =>
                "Cancelling objective audio observation extraction and closing the analysis-audio file…",
            _ => "Cancelling media ingest and closing open files…",
        };
        _activeProcessingCancellation.Cancel();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        DisposePreparedMediaReview();
        DisposeProcessingHistory();
        if (_processingCanBeCancelled)
        {
            _activeProcessingCancellation?.Cancel();
        }
    }

    private async void ProcessData_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcessingCancellation is not null)
        {
            StatusText.Text = "A processing job is already running in this app window.";
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
        var mediaIntegrityStateRecorded = false;
        var promotedAssets = new List<PromotedLocalMediaAsset>();
        CancellationTokenSource? heartbeatStop = null;
        Task<Exception?>? heartbeatTask = null;
        Exception? heartbeatFailure = null;
        var heartbeatAwaited = false;
        using var progressWriteGate = new SemaphoreSlim(1, 1);

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
        selected.SetLiveStatus("Preparing processing…");
        StatusText.Text = "Checking the selected profile and active MP4 media…";

        try
        {
            profileStore = CreateProfileStore(selected.WorkspaceRoot);
            var profile = await profileStore.GetByIdAsync(selected.Id, processingCancellation.Token)
                ?? throw new KeyNotFoundException("The selected profile no longer exists.");
            if (IsProcessingReadiness(profile.Readiness))
            {
                throw new ProfileProcessingActiveException(profile.Id);
            }

            layout = ProfileWorkspaceLayout.Create(profile.WorkspaceRoot, profile.DownloadStagingRoot);
            var pendingVideos = profile.TrainingVideos
                .Where(video => !video.IsArchived && video.MediaAssetId is null)
                .OrderBy(video => video.SortOrder)
                .ToArray();
            if (pendingVideos.Length == 0)
            {
                if (string.Equals(
                        profile.Readiness,
                        ProfileReadiness.MediaIntegrityFailed.ToString(),
                        StringComparison.Ordinal))
                {
                    selected.SetLiveStatus("Workspace media needs repair");
                    StatusText.Text = "Processing cannot continue because an active workspace media asset failed integrity verification.";
                    return;
                }

                if (string.Equals(
                        profile.Readiness,
                        ProfileReadiness.AudioObserved.ToString(),
                        StringComparison.Ordinal))
                {
                    selected.SetLiveStatus(null);
                    StatusText.Text = "Objective analysis-audio observations are already recorded for every active prepared asset. Quality and model applicability remain not assessed; no scoring was performed.";
                    return;
                }

                if (profile.Readiness is nameof(ProfileReadiness.MediaPrepared)
                    or nameof(ProfileReadiness.AudioObservationFailed))
                {
                    await RunAudioObservationExtractionAsync(
                        selected,
                        profileStore,
                        profile,
                        layout,
                        processingCancellation);
                }
                else if (profile.Readiness is nameof(ProfileReadiness.MediaValidated)
                    or nameof(ProfileReadiness.MediaPreprocessingFailed))
                {
                    await RunMediaPreprocessingAsync(
                        selected,
                        profileStore,
                        profile,
                        layout,
                        processingCancellation);
                }
                else
                {
                    await RunMediaValidationAsync(
                        selected,
                        profileStore,
                        profile,
                        layout,
                        processingCancellation);
                }

                return;
            }

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
                        mediaIntegrityStateRecorded = await TryPersistMediaIntegrityFailureAsync(
                            profileStore,
                            profile.Id,
                            [registeredAsset.Id]);
                        throw new InvalidDataException(
                            "A registered workspace media copy is missing or no longer matches its recorded integrity metadata.",
                            exception);
                    }
                }
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
            _activeProcessingKind = ProcessingJobKind.LocalMediaIngest;

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
                progressWriteGate,
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

            if (!await UpdateProcessingJobProgressSerializedAsync(
                    profileStore,
                    progressWriteGate,
                    jobId,
                    ProcessingJobState.Running,
                    pendingVideos.Length,
                    totalBytes,
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

            var refreshWarning = jobStarted || mediaIntegrityStateRecorded
                ? await TryReloadProfilesAfterProcessingAsync(selected.Id)
                : null;
            StatusText.Text = mediaIntegrityFailure
                ? mediaIntegrityStateRecorded
                    ? "Workspace media verification failed. The profile is marked as needing repair. Archive every selection linked to the affected media asset only to exclude it from the active set; automatic replacement is not implemented yet."
                    : "Workspace media verification failed, but the repair-required state could not be saved. Refresh the profile and do not continue processing it."
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
                _activeProcessingKind = null;
            }

            _processingCanBeCancelled = false;

            selected.SetLiveStatus(mediaIntegrityStateRecorded
                ? "Workspace media needs repair"
                : mediaIntegrityFailure
                    ? "Workspace media verification failed"
                    : null);
            SetProcessingUiState(isProcessing: false);
        }
    }

    private async Task RunMediaValidationAsync(
        ProfileSummaryViewModel selected,
        SqliteProfileStore profileStore,
        StoredProfile profile,
        ProfileWorkspaceLayout layout,
        CancellationTokenSource processingCancellation)
    {
        _activeProcessingKind = ProcessingJobKind.MediaValidation;
        var activeAssetIds = profile.TrainingVideos
            .Where(video => !video.IsArchived && video.MediaAssetId is not null)
            .Select(video => video.MediaAssetId!.Value)
            .Distinct()
            .ToArray();
        if (activeAssetIds.Length == 0)
        {
            selected.SetLiveStatus(null);
            StatusText.Text = "No active registered media is available for validation.";
            return;
        }

        Dictionary<Guid, StoredMediaAsset> assetsById;
        try
        {
            assetsById = (await profileStore.GetMediaAssetsAsync(
                    profile.Id,
                    processingCancellation.Token))
                .ToDictionary(asset => asset.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            selected.SetLiveStatus(null);
            StatusText.Text = "Registered media metadata could not be loaded. No validation job was created; refresh the profile and try again.";
            return;
        }
        if (activeAssetIds.Any(assetId => !assetsById.ContainsKey(assetId)))
        {
            selected.SetLiveStatus("Workspace media needs repair");
            StatusText.Text = "Media validation did not start because registered media metadata is incomplete.";
            return;
        }

        if (activeAssetIds.All(assetId => assetsById[assetId].State == MediaAssetState.Validated))
        {
            try
            {
                selected.SetLiveStatus("Verifying registered workspace media…");
                await VerifyRegisteredMediaAssetsAsync(
                    layout,
                    activeAssetIds.Select(assetId => assetsById[assetId]),
                    processingCancellation.Token);
                selected.SetLiveStatus(null);
                StatusText.Text = "All active workspace copies still match their recorded integrity metadata and already have immutable media-validation results. No analysis or scoring was performed.";
            }
            catch (OperationCanceledException)
            {
                selected.SetLiveStatus(null);
                StatusText.Text = "Media-validation verification cancelled. No profile or media was changed.";
            }
            catch (RegisteredMediaIntegrityException exception)
            {
                var integrityStateRecorded = await TryPersistMediaIntegrityFailureAsync(
                    profileStore,
                    profile.Id,
                    [exception.MediaAssetId]);
                var refreshWarning = integrityStateRecorded
                    ? await TryReloadProfilesAfterProcessingAsync(profile.Id)
                    : null;
                selected.SetLiveStatus(integrityStateRecorded
                    ? "Workspace media needs repair"
                    : "Workspace media verification failed");
                StatusText.Text = integrityStateRecorded
                    ? "The workspace media copy changed or is missing. The profile is marked as needing repair. Archive every selection linked to the affected media asset only to exclude it from the active set; automatic replacement is not implemented yet."
                    : "The workspace media copy changed or is missing, but the repair-required state could not be saved. Refresh the profile and do not continue processing it.";
                if (refreshWarning is not null)
                {
                    StatusText.Text += " The profile list could not be refreshed.";
                }
            }

            return;
        }

        var jobId = Guid.NewGuid();
        var startedAtUtc = NextTimestamp(profile.UpdatedAtUtc);
        var relativeJobPath = BuildMediaValidationJobRelativePath(jobId, startedAtUtc);
        string? jobDirectoryPath = null;
        var jobStarted = false;
        var completionCommitted = false;
        Guid? integrityFailedAssetId = null;
        CancellationTokenSource? heartbeatStop = null;
        Task<Exception?>? heartbeatTask = null;
        Exception? heartbeatFailure = null;
        var heartbeatAwaited = false;
        using var progressWriteGate = new SemaphoreSlim(1, 1);
        long latestCompletedBytes = 0;
        var latestCompletedItems = 0;

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

        try
        {
            var configuredTools = MediaToolchainConfiguration.Load();
            var toolContract = CreateMediaValidationToolContract(configuredTools);

            selected.SetLiveStatus("Checking approved FFmpeg tools…");
            StatusText.Text = "Verifying the pinned FFmpeg and ffprobe executables before creating a validation job…";
            var preflight = await _mediaValidationService.PreflightAsync(
                layout,
                layout.ProcessingRoot,
                toolContract,
                processingCancellation.Token);

            selected.SetLiveStatus("Verifying registered workspace media…");
            StatusText.Text = "The approved tools match. Rechecking active workspace copies before the validation job starts…";
            await VerifyRegisteredMediaAssetsAsync(
                layout,
                activeAssetIds.Select(assetId => assetsById[assetId]),
                processingCancellation.Token);

            await profileStore.StartMediaValidationJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                jobId,
                relativeJobPath,
                startedAtUtc,
                processingCancellation.Token);
            jobStarted = true;
            _activeProcessingKind = ProcessingJobKind.MediaValidation;
            jobDirectoryPath = CreateMediaValidationJobDirectory(
                layout,
                relativeJobPath);

            if (!await profileStore.UpdateProcessingJobProgressAsync(
                    jobId,
                    ProcessingJobState.Running,
                    0,
                    0,
                    NextTimestamp(startedAtUtc),
                    processingCancellation.Token))
            {
                throw new InvalidOperationException(
                    "The media-validation job stopped before probing began.");
            }

            var jobAssets = await profileStore.GetMediaAssetsForValidationJobAsync(
                jobId,
                processingCancellation.Token);
            if (jobAssets.Count == 0)
            {
                throw new InvalidOperationException(
                    "The media-validation job has no registered media snapshot.");
            }

            heartbeatStop = new CancellationTokenSource();
            heartbeatTask = RunProcessingHeartbeatAsync(
                profileStore,
                jobId,
                () => (
                    Volatile.Read(ref latestCompletedItems),
                    Volatile.Read(ref latestCompletedBytes)),
                progressWriteGate,
                processingCancellation,
                heartbeatStop.Token);

            var registrations = new List<MediaValidationRegistration>(jobAssets.Count);
            for (var index = 0; index < jobAssets.Count; index++)
            {
                processingCancellation.Token.ThrowIfCancellationRequested();
                var asset = jobAssets[index];
                var itemNumber = index + 1;
                var liveStatus = $"Validating MP4 {itemNumber}/{jobAssets.Count} · full CPU decode";
                selected.SetLiveStatus(liveStatus);
                StatusText.Text = liveStatus + ". This may take as long as the video; Cancel Processing remains available.";

                try
                {
                    var mediaPath = ResolveWorkspaceMediaPath(layout, asset.WorkspaceRelativePath);
                    var metadata = await _mediaValidationService.ValidateAsync(
                        layout,
                        jobDirectoryPath,
                        mediaPath,
                        asset.Sha256,
                        asset.ByteLength,
                        toolContract,
                        preflight,
                        processingCancellation.Token);
                    var validatedAtUtc = NextTimestamp(startedAtUtc);
                    registrations.Add(new(
                        asset.Id,
                        MediaAssetState.Validated,
                        MapValidationResult(asset.Id, metadata, validatedAtUtc),
                        FailureMessage: null));
                }
                catch (MediaValidationException exception) when (
                    IsMediaContentValidationFailure(exception.Failure))
                {
                    registrations.Add(new(
                        asset.Id,
                        MediaAssetState.ValidationFailed,
                        Result: null,
                        exception.Failure.ToString()));
                }
                catch (MediaValidationException exception) when (
                    exception.Failure is MediaValidationFailure.IntegrityChanged
                        or MediaValidationFailure.MediaPathInvalid)
                {
                    integrityFailedAssetId = asset.Id;
                    throw;
                }

                Interlocked.Exchange(ref latestCompletedItems, itemNumber);
                Interlocked.Add(ref latestCompletedBytes, asset.ByteLength);
                if (!await UpdateProcessingJobProgressSerializedAsync(
                        profileStore,
                        progressWriteGate,
                        jobId,
                        ProcessingJobState.Running,
                        itemNumber,
                        Volatile.Read(ref latestCompletedBytes),
                        processingCancellation.Token))
                {
                    throw new InvalidOperationException(
                        "The media-validation job stopped before all results were recorded.");
                }
            }

            var heartbeatError = await StopHeartbeatAsync();
            if (heartbeatError is not null)
            {
                throw new IOException(
                    "Media-validation progress could not be persisted.",
                    heartbeatError);
            }

            var latestResultTimestamp = registrations
                .Where(registration => registration.Result is not null)
                .Select(registration => registration.Result!.ValidatedAtUtc)
                .DefaultIfEmpty(startedAtUtc)
                .Max();
            await profileStore.CompleteMediaValidationJobAsync(
                jobId,
                registrations,
                NextTimestamp(latestResultTimestamp),
                processingCancellation.Token);
            completionCommitted = true;
            _processingCanBeCancelled = false;
            CancelProcessingButton.IsEnabled = false;

            await ReloadProfilesAsync(profile.Id);
            var failedRegistrations = registrations
                .Where(registration => registration.State == MediaAssetState.ValidationFailed)
                .ToArray();
            if (failedRegistrations.Length == 0)
            {
                StatusText.Text = "MP4 structure and the selected audio/video streams decoded completely. Media is validated and awaiting deterministic preprocessing; no analysis or scoring was performed.";
            }
            else
            {
                var failureCodes = string.Join(
                    ", ",
                    failedRegistrations
                        .Select(registration => registration.FailureMessage)
                        .Where(code => code is not null)
                        .Distinct(StringComparer.Ordinal));
                StatusText.Text = $"Media validation completed, but {failedRegistrations.Length} asset(s) need attention ({failureCodes}). No behavioral analysis or scoring was performed.";
            }
        }
        catch (OperationCanceledException)
        {
            var heartbeatError = await StopHeartbeatAsync();
            var terminalState = heartbeatError is null
                ? ProcessingJobState.Cancelled
                : ProcessingJobState.Failed;
            var terminalStateRecorded = true;
            if (jobStarted && !completionCommitted)
            {
                terminalStateRecorded = await TryTerminateProcessingJobAsync(
                    profileStore,
                    jobId,
                    terminalState,
                    heartbeatError is null
                        ? null
                        : "Media validation stopped because progress persistence failed.");
            }

            var refreshWarning = jobStarted
                ? await TryReloadProfilesAfterProcessingAsync(selected.Id)
                : null;
            StatusText.Text = jobStarted
                ? "Media validation cancelled. FFmpeg processes and open files were closed, no successful result was written, and the bounded Processing job folder was retained."
                : "Media-validation preparation cancelled. No processing job was created.";
            if (!terminalStateRecorded)
            {
                StatusText.Text += " The terminal job status could not be saved; use Refresh after the ten-minute recovery grace period.";
            }

            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed.";
            }
        }
        catch (RegisteredMediaIntegrityException exception)
        {
            var integrityStateRecorded = await TryPersistMediaIntegrityFailureAsync(
                profileStore,
                profile.Id,
                [exception.MediaAssetId]);
            var refreshWarning = integrityStateRecorded
                ? await TryReloadProfilesAfterProcessingAsync(profile.Id)
                : null;
            StatusText.Text = integrityStateRecorded
                ? "The workspace media copy changed or is missing. The profile is marked as needing repair; no validation job was created."
                : "The workspace media copy changed or is missing, but the repair-required state could not be saved. Refresh the profile and do not continue processing it.";
            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed.";
            }
        }
        catch (Exception exception) when (IsExpectedMediaValidationWorkflowException(exception))
        {
            await StopHeartbeatAsync();
            var terminalStateRecorded = true;
            if (jobStarted && !completionCommitted)
            {
                terminalStateRecorded = await TryTerminateProcessingJobAsync(
                    profileStore,
                    jobId,
                    ProcessingJobState.Failed,
                    "Media validation failed before completion.");
            }

            var integrityStateRecorded = false;
            if (integrityFailedAssetId is not null && terminalStateRecorded)
            {
                integrityStateRecorded = await TryPersistMediaIntegrityFailureAsync(
                    profileStore,
                    profile.Id,
                    [integrityFailedAssetId.Value]);
            }

            var refreshWarning = jobStarted || integrityStateRecorded
                ? await TryReloadProfilesAfterProcessingAsync(selected.Id)
                : null;
            StatusText.Text = integrityFailedAssetId is null
                ? GetSafeMediaValidationFailureMessage(
                    exception,
                    jobStarted,
                    completionCommitted)
                : integrityStateRecorded
                    ? "The workspace media copy changed during validation. The validation job stopped and the profile is marked as needing repair."
                    : "The workspace media copy changed during validation, but the repair-required state could not be saved. Refresh the profile and do not continue processing it.";
            if (!terminalStateRecorded)
            {
                StatusText.Text += " The failed job status could not be saved; use Refresh after the ten-minute recovery grace period.";
            }

            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed.";
            }
        }
        finally
        {
            await StopHeartbeatAsync();
            heartbeatStop?.Dispose();
        }
    }

    private async Task RunMediaPreprocessingAsync(
        ProfileSummaryViewModel selected,
        SqliteProfileStore profileStore,
        StoredProfile profile,
        ProfileWorkspaceLayout layout,
        CancellationTokenSource processingCancellation)
    {
        _activeProcessingKind = ProcessingJobKind.MediaPreprocessing;
        var activeAssetIds = profile.TrainingVideos
            .Where(video => !video.IsArchived && video.MediaAssetId is not null)
            .Select(video => video.MediaAssetId!.Value)
            .Distinct()
            .ToArray();
        if (activeAssetIds.Length == 0)
        {
            selected.SetLiveStatus(null);
            StatusText.Text = "No active registered media is available for preprocessing.";
            return;
        }

        Dictionary<Guid, StoredMediaAsset> assetsById;
        try
        {
            assetsById = (await profileStore.GetMediaAssetsAsync(
                    profile.Id,
                    processingCancellation.Token))
                .ToDictionary(asset => asset.Id);
        }
        catch (OperationCanceledException)
        {
            selected.SetLiveStatus(null);
            StatusText.Text = "Media-preprocessing preparation cancelled. No processing job or derivative was created.";
            return;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or DbException
                or FormatException)
        {
            selected.SetLiveStatus(null);
            StatusText.Text = "Registered media metadata could not be loaded. No preprocessing job was created; refresh the profile and try again.";
            return;
        }

        if (activeAssetIds.Any(assetId => !assetsById.ContainsKey(assetId)))
        {
            selected.SetLiveStatus("Workspace media needs repair");
            StatusText.Text = "Media preprocessing did not start because registered media metadata is incomplete.";
            return;
        }

        if (activeAssetIds.Any(assetId => assetsById[assetId].State == MediaAssetState.IntegrityFailed))
        {
            selected.SetLiveStatus("Workspace media needs repair");
            StatusText.Text = "Media preprocessing cannot continue because an active workspace asset failed integrity verification.";
            return;
        }

        if (activeAssetIds.All(assetId => assetsById[assetId].State == MediaAssetState.Prepared))
        {
            var failedAssetIds = new List<Guid>();
            var operationalVerificationFailure = false;
            try
            {
                selected.SetLiveStatus("Verifying prepared media artifacts…");
                await VerifyRegisteredMediaAssetsAsync(
                    layout,
                    activeAssetIds.Select(assetId => assetsById[assetId]),
                    processingCancellation.Token);
                foreach (var assetId in activeAssetIds)
                {
                    var storedResult = await profileStore.GetMediaPreprocessingResultAsync(
                        assetId,
                        processingCancellation.Token);
                    if (storedResult is null)
                    {
                        failedAssetIds.Add(assetId);
                        continue;
                    }

                    var verification = await _mediaPreprocessingService.VerifyPreparedAsync(
                        layout,
                        MapPreprocessingMetadata(storedResult),
                        processingCancellation.Token);
                    if (verification.State == MediaPreparedVerificationState.IntegrityMismatch)
                    {
                        failedAssetIds.Add(assetId);
                    }
                    else if (verification.State == MediaPreparedVerificationState.OperationalFailure)
                    {
                        operationalVerificationFailure = true;
                    }
                }

                if (failedAssetIds.Count == 0 && !operationalVerificationFailure)
                {
                    selected.SetLiveStatus(null);
                    StatusText.Text = "All prepared artifacts still match their immutable integrity metadata. Media quality and model applicability remain not assessed; no feature extraction, analysis, or scoring was performed.";
                    return;
                }

                if (failedAssetIds.Count == 0)
                {
                    selected.SetLiveStatus("Prepared-media verification unavailable");
                    StatusText.Text = "Prepared artifacts could not be read because of a temporary file-access problem. Their persisted integrity state was not changed; close other software using the files and select Refresh.";
                    return;
                }

                var integrityStateRecorded = await TryPersistMediaIntegrityFailureAsync(
                    profileStore,
                    profile.Id,
                    failedAssetIds);
                if (integrityStateRecorded)
                {
                    await TryReloadProfilesAfterProcessingAsync(profile.Id);
                }

                selected.SetLiveStatus(integrityStateRecorded
                    ? "Prepared media needs repair"
                    : "Prepared-media verification failed");
                StatusText.Text = integrityStateRecorded
                    ? "One or more prepared artifacts are missing or changed. The profile is marked as needing repair; no result was reused."
                    : "Prepared artifacts are missing or changed, but the repair-required state could not be saved. Refresh the profile and do not continue processing it.";
                if (operationalVerificationFailure)
                {
                    StatusText.Text += " At least one other prepared bundle was temporarily unreadable and was not marked as damaged.";
                }
            }
            catch (OperationCanceledException)
            {
                selected.SetLiveStatus(null);
                StatusText.Text = "Prepared-media verification cancelled. No profile or artifact was changed.";
            }
            catch (RegisteredMediaIntegrityException exception)
            {
                var integrityStateRecorded = await TryPersistMediaIntegrityFailureAsync(
                    profileStore,
                    profile.Id,
                    [exception.MediaAssetId]);
                if (integrityStateRecorded)
                {
                    await TryReloadProfilesAfterProcessingAsync(profile.Id);
                }

                selected.SetLiveStatus(integrityStateRecorded
                    ? "Workspace media needs repair"
                    : "Workspace media verification failed");
                StatusText.Text = integrityStateRecorded
                    ? "The validated original is missing or changed. The profile is marked as needing repair; no prepared result was reused."
                    : "The validated original is missing or changed, but the repair-required state could not be saved. Refresh the profile and do not continue processing it.";
            }

            return;
        }

        if (activeAssetIds.Any(assetId => assetsById[assetId].State is not (
                MediaAssetState.Validated or MediaAssetState.PreprocessingFailed or MediaAssetState.Prepared)))
        {
            selected.SetLiveStatus(null);
            StatusText.Text = "Media preprocessing cannot start until every active asset has passed MP4 validation.";
            return;
        }

        var jobId = Guid.NewGuid();
        var startedAtUtc = NextTimestamp(profile.UpdatedAtUtc);
        var relativeJobPath = BuildMediaPreprocessingJobRelativePath(jobId, startedAtUtc);
        string? jobDirectoryPath = null;
        var jobStarted = false;
        var completionCommitted = false;
        Guid? integrityFailedAssetId = null;
        var promotedResults = new List<PromotedMediaPreprocessingResult>();
        CancellationTokenSource? heartbeatStop = null;
        Task<Exception?>? heartbeatTask = null;
        Exception? heartbeatFailure = null;
        var heartbeatAwaited = false;
        using var progressWriteGate = new SemaphoreSlim(1, 1);
        long latestCompletedBytes = 0;
        var latestCompletedItems = 0;

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

        try
        {
            var configuredTools = MediaToolchainConfiguration.Load();
            if (!string.Equals(
                    configuredTools.PreprocessingContractVersion,
                    MediaPreprocessingService.CurrentPreprocessingContractVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The media preprocessing contract does not match this application build.");
            }

            var toolContract = CreateMediaValidationToolContract(configuredTools);
            selected.SetLiveStatus("Checking approved FFmpeg tools…");
            StatusText.Text = "Verifying the pinned FFmpeg and ffprobe executables before creating a preprocessing job…";
            var preflight = await _mediaValidationService.PreflightAsync(
                layout,
                layout.ProcessingRoot,
                toolContract,
                processingCancellation.Token);

            selected.SetLiveStatus("Verifying registered workspace media…");
            StatusText.Text = "The approved tools match. Rechecking validated originals before preprocessing starts…";
            await VerifyRegisteredMediaAssetsAsync(
                layout,
                activeAssetIds.Select(assetId => assetsById[assetId]),
                processingCancellation.Token);

            await profileStore.StartMediaPreprocessingJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                jobId,
                relativeJobPath,
                startedAtUtc,
                processingCancellation.Token);
            jobStarted = true;
            jobDirectoryPath = CreateProcessingJobDirectory(
                layout,
                relativeJobPath,
                "media-preprocessing");

            if (!await profileStore.UpdateProcessingJobProgressAsync(
                    jobId,
                    ProcessingJobState.Running,
                    0,
                    0,
                    NextTimestamp(startedAtUtc),
                    processingCancellation.Token))
            {
                throw new InvalidOperationException(
                    "The media-preprocessing job stopped before artifact generation began.");
            }

            var jobAssets = await profileStore.GetMediaAssetsForPreprocessingJobAsync(
                jobId,
                processingCancellation.Token);
            if (jobAssets.Count == 0)
            {
                throw new InvalidOperationException(
                    "The media-preprocessing job has no registered media snapshot.");
            }

            heartbeatStop = new CancellationTokenSource();
            heartbeatTask = RunProcessingHeartbeatAsync(
                profileStore,
                jobId,
                () => (
                    Volatile.Read(ref latestCompletedItems),
                    Volatile.Read(ref latestCompletedBytes)),
                progressWriteGate,
                processingCancellation,
                heartbeatStop.Token);

            var registrations = new List<MediaPreprocessingRegistration>(jobAssets.Count);
            for (var index = 0; index < jobAssets.Count; index++)
            {
                processingCancellation.Token.ThrowIfCancellationRequested();
                var asset = jobAssets[index];
                var itemNumber = index + 1;
                var validationResult = await profileStore.GetMediaValidationResultAsync(
                    asset.Id,
                    processingCancellation.Token)
                    ?? throw new InvalidDataException(
                        "A snapshotted media asset has no immutable validation result.");
                var itemFinished = 0;
                var progress = new InlineProgress<MediaPreprocessingProgress>(update =>
                {
                    var phase = DescribeMediaPreprocessingPhase(update.Phase);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_activeProcessingProfileId != selected.Id
                            || Volatile.Read(ref itemFinished) != 0)
                        {
                            return;
                        }

                        var liveStatus = $"Preparing media {itemNumber}/{jobAssets.Count} · {phase}";
                        selected.SetLiveStatus(liveStatus);
                        StatusText.Text = liveStatus + ". The validated original remains unchanged; Cancel Processing remains available.";
                    });
                });

                try
                {
                    var staged = await _mediaPreprocessingService.PrepareAsync(
                        layout,
                        jobDirectoryPath,
                        new(
                            jobId,
                            asset.Id,
                            ResolveWorkspaceMediaPath(layout, asset.WorkspaceRelativePath),
                            asset.Sha256,
                            asset.ByteLength,
                            MapValidationMetadata(validationResult)),
                        toolContract,
                        preflight,
                        progress,
                        processingCancellation.Token);
                    var promoted = await _mediaPreprocessingService.PromoteAsync(
                        layout,
                        staged,
                        processingCancellation.Token);
                    promotedResults.Add(promoted);
                    registrations.Add(new(
                        asset.Id,
                        MediaAssetState.Prepared,
                        MapPreprocessingResult(promoted.Output),
                        FailureMessage: null));
                }
                catch (MediaPreprocessingException exception) when (
                    IsDeterministicMediaPreprocessingFailure(exception.Failure))
                {
                    registrations.Add(new(
                        asset.Id,
                        MediaAssetState.PreprocessingFailed,
                        Result: null,
                        exception.Failure.ToString()));
                }
                catch (MediaPreprocessingException exception) when (
                    exception.Failure is MediaPreprocessingFailure.SourceIntegrityInvalid
                        or MediaPreprocessingFailure.SourceIntegrityChanged
                        or MediaPreprocessingFailure.MediaPathInvalid)
                {
                    integrityFailedAssetId = asset.Id;
                    throw;
                }
                finally
                {
                    Interlocked.Exchange(ref itemFinished, 1);
                }

                Interlocked.Exchange(ref latestCompletedItems, itemNumber);
                Interlocked.Add(ref latestCompletedBytes, asset.ByteLength);
                if (!await UpdateProcessingJobProgressSerializedAsync(
                        profileStore,
                        progressWriteGate,
                        jobId,
                        ProcessingJobState.Running,
                        itemNumber,
                        Volatile.Read(ref latestCompletedBytes),
                        processingCancellation.Token))
                {
                    throw new InvalidOperationException(
                        "The media-preprocessing job stopped before all results were recorded.");
                }
            }

            var heartbeatError = await StopHeartbeatAsync();
            if (heartbeatError is not null)
            {
                throw new IOException(
                    "Media-preprocessing progress could not be persisted.",
                    heartbeatError);
            }

            var latestResultTimestamp = registrations
                .Where(registration => registration.Result is not null)
                .Select(registration => registration.Result!.PreprocessedAtUtc)
                .DefaultIfEmpty(startedAtUtc)
                .Max();
            processingCancellation.Token.ThrowIfCancellationRequested();
            _processingCanBeCancelled = false;
            CancelProcessingButton.IsEnabled = false;
            selected.SetLiveStatus("Finalizing prepared media…");
            StatusText.Text = "Artifact generation is complete. Committing the immutable preprocessing results…";
            await profileStore.CompleteMediaPreprocessingJobAsync(
                jobId,
                registrations,
                NextTimestamp(latestResultTimestamp),
                CancellationToken.None);
            completionCommitted = true;

            var promotionCleanupWarning = false;
            foreach (var promoted in promotedResults)
            {
                try
                {
                    _mediaPreprocessingService.ConfirmPromotion(layout, promoted);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    promotionCleanupWarning = true;
                }
            }

            await ReloadProfilesAsync(profile.Id);
            var failedRegistrations = registrations
                .Where(registration => registration.State == MediaAssetState.PreprocessingFailed)
                .ToArray();
            if (failedRegistrations.Length == 0)
            {
                StatusText.Text = promotionCleanupWarning
                    ? "Preprocessing results were saved, but a promotion journal needs inspection in the Processing folder. Prepared artifacts will be reconciled on restart."
                    : "Playback proxy, mono analysis audio, and timestamp mapping were prepared and hashed. Engineering preprocessing is complete; media quality and model applicability remain not assessed. No feature extraction, analysis, or scoring was performed.";
            }
            else
            {
                var failureCodes = string.Join(
                    ", ",
                    failedRegistrations
                        .Select(registration => registration.FailureMessage)
                        .Where(code => code is not null)
                        .Distinct(StringComparer.Ordinal));
                StatusText.Text = $"Media preprocessing completed, but {failedRegistrations.Length} asset(s) need attention ({failureCodes}). No successful artifacts were accepted for those assets; quality and applicability remain not assessed.";
            }
        }
        catch (OperationCanceledException)
        {
            var heartbeatError = await StopHeartbeatAsync();
            var rollbackSucceeded = completionCommitted
                || RollbackPreprocessingPromotions(layout, promotedResults);
            var terminalState = heartbeatError is null
                ? ProcessingJobState.Cancelled
                : ProcessingJobState.Failed;
            var terminalStateRecorded = true;
            if (jobStarted && !completionCommitted)
            {
                terminalStateRecorded = await TryTerminateProcessingJobAsync(
                    profileStore,
                    jobId,
                    terminalState,
                    heartbeatError is null
                        ? null
                        : "Media preprocessing stopped because progress persistence failed.");
            }

            var refreshWarning = jobStarted
                ? await TryReloadProfilesAfterProcessingAsync(selected.Id)
                : null;
            StatusText.Text = jobStarted
                ? "Media preprocessing cancelled. FFmpeg processes and open files were closed, no successful preprocessing result was written, and the bounded Processing job folder was retained."
                : "Media-preprocessing preparation cancelled. No processing job or derivative was created.";
            if (jobStarted && rollbackSucceeded)
            {
                StatusText.Text += " No partial derivative remains promoted.";
            }
            else if (!rollbackSucceeded)
            {
                StatusText.Text += " A promoted derivative could not be returned to the Processing job; restart the app to reconcile its journal.";
            }

            if (!terminalStateRecorded)
            {
                StatusText.Text += " The terminal job status could not be saved; use Refresh after the ten-minute recovery grace period.";
            }

            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed.";
            }
        }
        catch (RegisteredMediaIntegrityException exception)
        {
            var integrityStateRecorded = await TryPersistMediaIntegrityFailureAsync(
                profileStore,
                profile.Id,
                [exception.MediaAssetId]);
            var refreshWarning = integrityStateRecorded
                ? await TryReloadProfilesAfterProcessingAsync(profile.Id)
                : null;
            StatusText.Text = integrityStateRecorded
                ? "The validated original changed or is missing. The profile is marked as needing repair; no preprocessing job was created."
                : "The validated original changed or is missing, but the repair-required state could not be saved. Refresh the profile and do not continue processing it.";
            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed.";
            }
        }
        catch (Exception exception) when (IsExpectedMediaPreprocessingWorkflowException(exception))
        {
            await StopHeartbeatAsync();
            var rollbackSucceeded = completionCommitted
                || RollbackPreprocessingPromotions(layout, promotedResults);
            var terminalStateRecorded = true;
            if (jobStarted && !completionCommitted)
            {
                terminalStateRecorded = await TryTerminateProcessingJobAsync(
                    profileStore,
                    jobId,
                    ProcessingJobState.Failed,
                    "Media preprocessing failed before completion.");
            }

            var integrityStateRecorded = false;
            if (integrityFailedAssetId is not null && terminalStateRecorded)
            {
                integrityStateRecorded = await TryPersistMediaIntegrityFailureAsync(
                    profileStore,
                    profile.Id,
                    [integrityFailedAssetId.Value]);
            }

            var refreshWarning = jobStarted || integrityStateRecorded
                ? await TryReloadProfilesAfterProcessingAsync(selected.Id)
                : null;
            StatusText.Text = integrityFailedAssetId is null
                ? GetSafeMediaPreprocessingFailureMessage(
                    exception,
                    jobStarted,
                    completionCommitted)
                : integrityStateRecorded
                    ? "The validated original changed during preprocessing. The job stopped, no derivative was accepted, and the profile is marked as needing repair."
                    : "The validated original changed during preprocessing, but the repair-required state could not be saved. Refresh the profile and do not continue processing it.";
            if (!rollbackSucceeded)
            {
                StatusText.Text += " A promoted derivative could not be returned to the Processing job; restart the app to reconcile its journal.";
            }

            if (!terminalStateRecorded)
            {
                StatusText.Text += " The failed job status could not be saved; use Refresh after the ten-minute recovery grace period.";
            }

            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed.";
            }
        }
        finally
        {
            await StopHeartbeatAsync();
            heartbeatStop?.Dispose();
        }
    }

    private static MediaValidationToolContract CreateMediaValidationToolContract(
        ConfiguredMediaToolchain configuredTools) =>
        new(
            new MediaValidationExecutableContract(
                configuredTools.FfprobeExecutablePath,
                configuredTools.FfprobeSha256,
                PreflightTimeout: TimeSpan.FromSeconds(15),
                InvocationTimeout: TimeSpan.FromMinutes(2),
                MaximumStandardOutputBytes: 4 * 1024 * 1024,
                MaximumStandardErrorBytes: 256 * 1024),
            new MediaValidationExecutableContract(
                configuredTools.FfmpegExecutablePath,
                configuredTools.FfmpegSha256,
                PreflightTimeout: TimeSpan.FromSeconds(15),
                InvocationTimeout: TimeSpan.FromHours(2),
                MaximumStandardOutputBytes: 2 * 1024 * 1024,
                MaximumStandardErrorBytes: 1024 * 1024),
            configuredTools.BuildIdentity,
            configuredTools.ValidationContractVersion);

    private static string BuildMediaValidationJobRelativePath(
        Guid jobId,
        DateTimeOffset createdAtUtc)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("The processing job ID cannot be empty.", nameof(jobId));
        }

        var directoryName = string.Create(
            CultureInfo.InvariantCulture,
            $"{createdAtUtc.ToUniversalTime():yyyyMMdd'T'HHmmssfffffff'Z'}_media-validation_{jobId.ToString("N")[..12]}");
        return Path.Combine("Processing", directoryName);
    }

    private static string BuildMediaPreprocessingJobRelativePath(
        Guid jobId,
        DateTimeOffset createdAtUtc)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("The processing job ID cannot be empty.", nameof(jobId));
        }

        var directoryName = string.Create(
            CultureInfo.InvariantCulture,
            $"{createdAtUtc.ToUniversalTime():yyyyMMdd'T'HHmmssfffffff'Z'}_media-preprocessing_{jobId.ToString("N")[..12]}");
        return Path.Combine("Processing", directoryName);
    }

    private static string CreateMediaValidationJobDirectory(
        ProfileWorkspaceLayout layout,
        string workspaceRelativePath) =>
        CreateProcessingJobDirectory(layout, workspaceRelativePath, "media-validation");

    private static string CreateProcessingJobDirectory(
        ProfileWorkspaceLayout layout,
        string workspaceRelativePath,
        string phaseName)
    {
        var path = Path.GetFullPath(Path.Combine(
            layout.WorkspaceRoot,
            workspaceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (Directory.GetParent(path) is not { } parent
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(parent.FullName),
                Path.TrimEndingDirectorySeparator(layout.ProcessingRoot),
                StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(path)
            || File.Exists(path))
        {
            throw new IOException($"The {phaseName} processing folder is not available.");
        }

        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The {phaseName} processing folder is invalid.");
        }

        using var claim = new FileStream(
            Path.Combine(path, ".job"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        claim.Flush(flushToDisk: true);
        return path;
    }

    private static string ResolveWorkspaceMediaPath(
        ProfileWorkspaceLayout layout,
        string workspaceRelativePath) =>
        Path.GetFullPath(Path.Combine(
            layout.WorkspaceRoot,
            workspaceRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    private async Task VerifyRegisteredMediaAssetsAsync(
        ProfileWorkspaceLayout layout,
        IEnumerable<StoredMediaAsset> assets,
        CancellationToken cancellationToken)
    {
        foreach (var asset in assets)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _localMediaStagingService.VerifyExistingAssetAsync(
                    layout,
                    asset.Id,
                    asset.WorkspaceRelativePath,
                    asset.Sha256,
                    asset.ByteLength,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or IOException
                    or UnauthorizedAccessException)
            {
                throw new RegisteredMediaIntegrityException(asset.Id);
            }
        }
    }

    private static StoredMediaValidationResult MapValidationResult(
        Guid mediaAssetId,
        ValidatedMediaMetadata metadata,
        DateTimeOffset validatedAtUtc) =>
        new(
            mediaAssetId,
            metadata.ContainerFormat,
            metadata.ContainerMajorBrand,
            metadata.Video.StreamIndex,
            metadata.Video.CodecName,
            metadata.Video.Width,
            metadata.Video.Height,
            metadata.Audio.StreamIndex,
            metadata.Audio.CodecName,
            metadata.Audio.SampleRateHz,
            metadata.Audio.ChannelCount,
            metadata.DurationMicroseconds,
            metadata.Video.FrameRateNumerator,
            metadata.Video.FrameRateDenominator,
            metadata.Ffprobe.Version,
            metadata.Ffprobe.CompilerIdentifier,
            metadata.Ffprobe.Configuration,
            metadata.Ffprobe.ConfigurationSha256,
            metadata.Ffprobe.ExecutableSha256,
            metadata.Ffmpeg.Version,
            metadata.Ffmpeg.CompilerIdentifier,
            metadata.Ffmpeg.Configuration,
            metadata.Ffmpeg.ConfigurationSha256,
            metadata.Ffmpeg.ExecutableSha256,
            metadata.ValidationContractSha256,
            metadata.DecodeCompleted,
            metadata.DecodedDurationMicroseconds,
            validatedAtUtc);

    private static ValidatedMediaMetadata MapValidationMetadata(
        StoredMediaValidationResult result) =>
        new(
            result.ContainerFormat,
            result.ContainerMajorBrand,
            result.DurationMicroseconds,
            new(
                result.VideoStreamIndex,
                result.VideoCodec,
                result.Width,
                result.Height,
                result.FrameRateNumerator,
                result.FrameRateDenominator),
            new(
                result.AudioStreamIndex,
                result.AudioCodec,
                result.AudioSampleRateHz,
                result.AudioChannelCount),
            new(
                result.FfprobeVersion,
                result.FfprobeCompilerIdentifier,
                result.FfprobeConfiguration,
                result.FfprobeConfigurationSha256,
                result.FfprobeExecutableSha256),
            new(
                result.FfmpegVersion,
                result.FfmpegCompilerIdentifier,
                result.FfmpegConfiguration,
                result.FfmpegConfigurationSha256,
                result.FfmpegExecutableSha256),
            result.ValidationContractSha256,
            result.DecodedDurationMicroseconds);

    private static StoredMediaPreprocessingResult MapPreprocessingResult(
        MediaPreprocessingResult result) =>
        new(
            result.MediaAssetId,
            result.SourceSha256,
            result.SourceByteLength,
            result.PreprocessingContractVersion,
            result.PreprocessingContractSha256,
            result.ProxyWorkspaceRelativePath,
            result.ProxySha256,
            result.ProxyByteLength,
            result.ProxyContainerFormat,
            result.ProxyVideoCodec,
            result.ProxyPixelFormat,
            result.ProxyWidth,
            result.ProxyHeight,
            result.ProxyFrameRateNumerator,
            result.ProxyFrameRateDenominator,
            result.ProxyAudioCodec,
            result.ProxyAudioSampleRateHz,
            result.ProxyAudioChannelCount,
            result.ProxyDurationMicroseconds,
            result.AnalysisAudioWorkspaceRelativePath,
            result.AnalysisAudioSha256,
            result.AnalysisAudioByteLength,
            result.AnalysisAudioCodec,
            result.AnalysisAudioSampleRateHz,
            result.AnalysisAudioChannelCount,
            result.AnalysisAudioSampleCount,
            result.AnalysisAudioDurationMicroseconds,
            result.TimestampMapWorkspaceRelativePath,
            result.TimestampMapSha256,
            result.TimestampMapByteLength,
            result.ManifestWorkspaceRelativePath,
            result.ManifestSha256,
            result.ManifestByteLength,
            result.SourceTimelineOriginMicroseconds,
            result.MappedDurationMicroseconds,
            result.VideoMapEntryCount,
            result.AudioMapSegmentCount,
            result.FfmpegVersion,
            result.FfmpegCompilerIdentifier,
            result.FfmpegConfigurationSha256,
            result.FfmpegExecutableSha256,
            result.MediaValidationContractSha256,
            Enum.Parse<MediaQualityState>(result.MediaQualityState, ignoreCase: false),
            Enum.Parse<ModelApplicabilityState>(result.ModelApplicabilityState, ignoreCase: false),
            result.PreprocessedAtUtc);

    private static MediaPreprocessingResult MapPreprocessingMetadata(
        StoredMediaPreprocessingResult result) =>
        new(
            result.MediaAssetId,
            result.SourceSha256,
            result.SourceByteLength,
            result.PreprocessingContractVersion,
            result.PreprocessingContractSha256,
            result.ProxyWorkspaceRelativePath,
            result.ProxySha256,
            result.ProxyByteLength,
            result.ProxyContainerFormat,
            result.ProxyVideoCodec,
            result.ProxyPixelFormat,
            result.ProxyWidth,
            result.ProxyHeight,
            result.ProxyFrameRateNumerator,
            result.ProxyFrameRateDenominator,
            result.ProxyAudioCodec,
            result.ProxyAudioSampleRateHz,
            result.ProxyAudioChannelCount,
            result.ProxyDurationMicroseconds,
            result.AnalysisAudioWorkspaceRelativePath,
            result.AnalysisAudioSha256,
            result.AnalysisAudioByteLength,
            result.AnalysisAudioCodec,
            result.AnalysisAudioSampleRateHz,
            result.AnalysisAudioChannelCount,
            result.AnalysisAudioSampleCount,
            result.AnalysisAudioDurationMicroseconds,
            result.TimestampMapWorkspaceRelativePath,
            result.TimestampMapSha256,
            result.TimestampMapByteLength,
            result.ManifestWorkspaceRelativePath,
            result.ManifestSha256,
            result.ManifestByteLength,
            result.SourceTimelineOriginMicroseconds,
            result.MappedDurationMicroseconds,
            result.VideoMapEntryCount,
            result.AudioMapSegmentCount,
            result.FfmpegVersion,
            result.FfmpegCompilerIdentifier,
            result.FfmpegConfigurationSha256,
            result.FfmpegExecutableSha256,
            result.MediaValidationContractSha256,
            result.MediaQualityState.ToString(),
            result.ModelApplicabilityState.ToString(),
            result.PreprocessedAtUtc);

    private static string DescribeMediaPreprocessingPhase(MediaPreprocessingPhase phase) =>
        phase switch
        {
            MediaPreprocessingPhase.ProbingTimeline => "mapping source time",
            MediaPreprocessingPhase.GeneratingArtifacts => "playback proxy and analysis audio",
            MediaPreprocessingPhase.VerifyingArtifacts => "verifying artifact formats",
            MediaPreprocessingPhase.HashingArtifacts => "hashing artifacts",
            MediaPreprocessingPhase.WritingManifests => "timestamp map and manifest",
            MediaPreprocessingPhase.Completed => "finalizing",
            _ => "preparing artifacts",
        };

    private static bool IsDeterministicMediaPreprocessingFailure(
        MediaPreprocessingFailure failure) =>
        failure is MediaPreprocessingFailure.TimelineProbeFailed
            or MediaPreprocessingFailure.TimelineProbeMalformed
            or MediaPreprocessingFailure.GenerationFailed
            or MediaPreprocessingFailure.GenerationProgressMalformed
            or MediaPreprocessingFailure.ArtifactProbeFailed
            or MediaPreprocessingFailure.ArtifactProbeMalformed
            or MediaPreprocessingFailure.ArtifactContractMismatch;

    private static bool IsExpectedMediaPreprocessingWorkflowException(Exception exception) =>
        exception is MediaPreprocessingException
            or MediaValidationException
            or ArgumentException
            or ArithmeticException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or KeyNotFoundException
            or DbException
            or FormatException
            or JsonException
            or CryptographicException;

    private static string GetSafeMediaPreprocessingFailureMessage(
        Exception exception,
        bool jobStarted,
        bool completionCommitted)
    {
        if (completionCommitted)
        {
            return "Media-preprocessing results were saved, but the refreshed profile list is unavailable. Restart or select Refresh; do not retry merely to refresh the display.";
        }

        if (exception is MediaValidationException or MediaPreprocessingException)
        {
            var toolFailure = exception switch
            {
                MediaValidationException validation =>
                    validation.Failure is MediaValidationFailure.ToolContractInvalid
                        or MediaValidationFailure.ToolUnavailable
                        or MediaValidationFailure.ToolIntegrityMismatch
                        or MediaValidationFailure.ToolIdentityMalformed
                        or MediaValidationFailure.ToolIdentityMismatch
                        or MediaValidationFailure.ToolLaunchFailed
                        or MediaValidationFailure.ToolIdentityTimedOut
                        or MediaValidationFailure.ToolIdentityOutputLimitExceeded,
                MediaPreprocessingException preprocessing =>
                    preprocessing.Failure is MediaPreprocessingFailure.ToolContractInvalid
                        or MediaPreprocessingFailure.ToolIntegrityMismatch
                        or MediaPreprocessingFailure.PreflightMismatch,
                _ => false,
            };
            if (toolFailure)
            {
                return "The approved FFmpeg tools were not found or do not match this build. Check the Media Tools setup in README.md. No preprocessing result was accepted.";
            }
        }

        if (!jobStarted
            && exception is (FileNotFoundException or InvalidDataException))
        {
            return "The approved FFmpeg tools or preprocessing contract are not configured for this build. Follow the Media Tools setup in README.md; the profile state was not changed.";
        }

        if (exception is ProfileProcessingActiveException)
        {
            return "This profile already has an active processing job, possibly in another app window. Refresh after it finishes.";
        }

        if (exception is ProfileConcurrencyConflictException)
        {
            return "The profile changed before media preprocessing could start. Refresh the profile list and try again.";
        }

        return jobStarted
            ? "Media preprocessing failed safely. No successful result was accepted; the Processing job folder was retained for inspection."
            : "Media preprocessing did not start. Check the local media-tool setup, refresh the profile, and try again.";
    }

    private static bool IsMediaContentValidationFailure(MediaValidationFailure failure) =>
        failure is MediaValidationFailure.ProbeRejectedMedia
            or MediaValidationFailure.UnsupportedContainer
            or MediaValidationFailure.InvalidDuration
            or MediaValidationFailure.MissingVideoStream
            or MediaValidationFailure.InvalidVideoStream
            or MediaValidationFailure.AmbiguousVideoStreams
            or MediaValidationFailure.MissingAudioStream
            or MediaValidationFailure.InvalidAudioStream
            or MediaValidationFailure.AmbiguousAudioStreams
            or MediaValidationFailure.UnsupportedCodec
            or MediaValidationFailure.CorruptMedia;

    private static bool IsExpectedMediaValidationWorkflowException(Exception exception) =>
        exception is MediaValidationException
            or ArgumentException
            or ArithmeticException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or KeyNotFoundException
            or DbException
            or FormatException
            or JsonException
            or CryptographicException;

    private static string GetSafeMediaValidationFailureMessage(
        Exception exception,
        bool jobStarted,
        bool completionCommitted)
    {
        if (completionCommitted)
        {
            return "Media-validation results were saved, but the refreshed profile list is unavailable. Restart or select Refresh; do not retry merely to refresh the display.";
        }

        if (exception is MediaValidationException validationException)
        {
            if (validationException.Failure is MediaValidationFailure.ToolContractInvalid
                or MediaValidationFailure.ToolUnavailable
                or MediaValidationFailure.ToolIntegrityMismatch
                or MediaValidationFailure.ToolIdentityMalformed
                or MediaValidationFailure.ToolIdentityMismatch
                or MediaValidationFailure.ToolLaunchFailed
                or MediaValidationFailure.ToolIdentityTimedOut
                or MediaValidationFailure.ToolIdentityOutputLimitExceeded)
            {
                return "The approved FFmpeg tools were not found or do not match this build. Check the Media Tools setup in README.md. No validation result was accepted.";
            }

            if (validationException.Failure is MediaValidationFailure.IntegrityChanged
                or MediaValidationFailure.MediaPathInvalid)
            {
                return "The workspace media copy changed or is no longer at its registered location. No validation result was accepted; keep the original source for a future repair workflow.";
            }

            return $"Media validation stopped safely ({validationException.Failure}). No validation result was accepted; retry after checking the retained Processing job folder.";
        }

        if (!jobStarted && exception is FileNotFoundException or JsonException)
        {
            return "The approved FFmpeg tools are not configured. Follow the Media Tools setup in README.md; the profile state was not changed.";
        }

        if (exception is ProfileProcessingActiveException)
        {
            return "This profile already has an active processing job, possibly in another app window. Refresh after it finishes.";
        }

        if (exception is ProfileConcurrencyConflictException)
        {
            return "The profile changed before media validation could start. Refresh the profile list and try again.";
        }

        return jobStarted
            ? "Media validation failed safely. No successful result was accepted; the Processing job folder was retained for inspection."
            : "Media validation did not start. Check the local media-tool setup, refresh the profile, and try again.";
    }

    private static DateTimeOffset NextTimestamp(DateTimeOffset floor)
    {
        var now = DateTimeOffset.UtcNow;
        return now <= floor ? floor.AddTicks(1) : now;
    }

    private static async Task<Exception?> RunProcessingHeartbeatAsync(
        SqliteProfileStore profileStore,
        Guid jobId,
        Func<(int CompletedItems, long CompletedBytes)> readProgress,
        SemaphoreSlim progressWriteGate,
        CancellationTokenSource processingCancellation,
        CancellationToken stopToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(stopToken))
            {
                await progressWriteGate.WaitAsync(stopToken);
                try
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
                            "The processing job is no longer active in profile storage.");
                    }
                }
                finally
                {
                    progressWriteGate.Release();
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

    private static async Task<bool> UpdateProcessingJobProgressSerializedAsync(
        SqliteProfileStore profileStore,
        SemaphoreSlim progressWriteGate,
        Guid jobId,
        ProcessingJobState state,
        int completedItems,
        long completedBytes,
        CancellationToken cancellationToken)
    {
        await progressWriteGate.WaitAsync(cancellationToken);
        try
        {
            return await profileStore.UpdateProcessingJobProgressAsync(
                jobId,
                state,
                completedItems,
                completedBytes,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        finally
        {
            progressWriteGate.Release();
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

    private bool RollbackPreprocessingPromotions(
        ProfileWorkspaceLayout layout,
        IEnumerable<PromotedMediaPreprocessingResult> promotedResults)
    {
        var allRolledBack = true;
        foreach (var promoted in promotedResults.Reverse())
        {
            try
            {
                _mediaPreprocessingService.RollbackPromotion(layout, promoted);
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

    private static async Task<bool> TryPersistMediaIntegrityFailureAsync(
        SqliteProfileStore profileStore,
        Guid profileId,
        IReadOnlyCollection<Guid> mediaAssetIds)
    {
        try
        {
            var currentProfile = await profileStore.GetByIdAsync(
                profileId,
                CancellationToken.None);
            if (currentProfile is null)
            {
                return false;
            }

            await profileStore.MarkMediaAssetsIntegrityFailedAsync(
                profileId,
                currentProfile.UpdatedAtUtc,
                mediaAssetIds,
                NextTimestamp(currentProfile.UpdatedAtUtc),
                CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or KeyNotFoundException
                or DbException
                or FormatException)
        {
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

    private void AddRecordingDependencyGroup_Click(object sender, RoutedEventArgs e) =>
        AddRecordingDependencyGroupFromInput();

    private void NewRecordingDependencyGroupName_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        AddRecordingDependencyGroupFromInput();
    }

    private void AddRecordingDependencyGroupFromInput()
    {
        var displayName = NewRecordingDependencyGroupNameBox.Text.Trim();
        if (displayName.Length == 0)
        {
            StatusText.Text = "Enter a recording dependency group name before adding it.";
            return;
        }

        if (string.Equals(displayName, "Unassigned", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Unassigned is reserved for videos without a recording dependency group.";
            return;
        }

        if (_recordingDependencyGroups.Any(group => string.Equals(
                group.DisplayName.Trim(),
                displayName,
                StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "Recording dependency group names must be unique within this profile.";
            return;
        }

        var group = new RecordingDependencyGroupOptionViewModel(Guid.NewGuid(), displayName);
        _recordingDependencyGroups.Add(group);
        _recordingDependencyGroupOptions.Add(group);
        NewRecordingDependencyGroupNameBox.Text = string.Empty;
        StatusText.Text = "Recording dependency group added to the detached draft. Save the profile to persist it.";
        UpdateRecordingDependencyGroupDraftSummary();
    }

    private void RecordingDependencyGroupName_BeforeTextChanging(
        TextBox sender,
        TextBoxBeforeTextChangingEventArgs e)
    {
        if (!string.Equals(e.NewText.Trim(), "Unassigned", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        StatusText.Text = "Unassigned is reserved for videos without a recording dependency group.";
    }

    private void RemoveRecordingDependencyGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RecordingDependencyGroupOptionViewModel group }
            || group.Id is null)
        {
            return;
        }

        var affectedVideos = AllVideos()
            .Where(video => video.RecordingDependencyGroupId == group.Id)
            .ToArray();

        var unassignedGroup = UnassignedRecordingDependencyGroup;
        _recordingDependencyGroups.Remove(group);
        _recordingDependencyGroupOptions.Remove(group);

        foreach (var video in affectedVideos)
        {
            video.SelectedRecordingDependencyGroup = unassignedGroup;
        }

        StatusText.Text = $"Recording dependency group removed from the detached draft; "
            + $"{affectedVideos.Length} selection(s) explicitly set to Unassigned.";
        UpdateRecordingDependencyGroupDraftSummary();
    }

    private void RecordingDependencyGroupSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: TrainingVideoItemViewModel video }
            && e.AddedItems.FirstOrDefault() is RecordingDependencyGroupOptionViewModel selectedGroup)
        {
            video.SelectedRecordingDependencyGroup = selectedGroup;
            UpdateRecordingDependencyGroupDraftSummary();
        }
    }

    private void RecordingDependencyGroupComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            QueueRecordingDependencyGroupSelectionSync(comboBox);
        }
    }

    private void RecordingDependencyGroupComboBox_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            QueueRecordingDependencyGroupSelectionSync(comboBox);
        }
    }

    private static void QueueRecordingDependencyGroupSelectionSync(ComboBox comboBox)
    {
        if (comboBox.DataContext is not TrainingVideoItemViewModel expectedVideo)
        {
            return;
        }

        comboBox.DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(comboBox.DataContext, expectedVideo)
                || !ReferenceEquals(
                    comboBox.ItemsSource,
                    expectedVideo.RecordingDependencyGroupOptions)
                || !expectedVideo.RecordingDependencyGroupOptions.Contains(
                    expectedVideo.SelectedRecordingDependencyGroup))
            {
                return;
            }

            comboBox.SelectedItem = expectedVideo.SelectedRecordingDependencyGroup;
        });
    }

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
        UpdateRecordingDependencyGroupDraftSummary();
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
        UpdateRecordingDependencyGroupDraftSummary();
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

        if (textBox.DataContext is not TrainingVideoItemViewModel video)
        {
            return;
        }

        video.RecordingDateLabel = textBox.Text;
        e.Handled = true;
        MoveFocusToNextRecordingDateLabel(video);
        StatusText.Text = "Recording date label accepted. It remains display/sort metadata only.";
    }

    private void MoveFocusToNextRecordingDateLabel(TrainingVideoItemViewModel currentVideo)
    {
        var videos = AllVideos().ToArray();
        var currentIndex = Array.IndexOf(videos, currentVideo);
        if (currentIndex < 0 || currentIndex >= videos.Length - 1)
        {
            return;
        }

        var nextVideo = videos[currentIndex + 1];
        var nextList = nextVideo.Condition == TrainingCondition.VerifiedSincereTruth
            ? TruthfulVideosList
            : DeceptionVideosList;

        nextList.ScrollIntoView(nextVideo);
        DispatcherQueue.TryEnqueue(() =>
        {
            nextList.UpdateLayout();
            if (nextList.ContainerFromItem(nextVideo) is DependencyObject container)
            {
                FindRecordingDateLabelTextBox(container, nextVideo)?.Focus(FocusState.Keyboard);
            }
        });
    }

    private static TextBox? FindRecordingDateLabelTextBox(
        DependencyObject root,
        TrainingVideoItemViewModel video)
    {
        if (root is TextBox textBox && ReferenceEquals(textBox.DataContext, video))
        {
            return textBox;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindRecordingDateLabelTextBox(VisualTreeHelper.GetChild(root, index), video);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
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
        var recordingDependencyGroups = _recordingDependencyGroups
            .Select(group => new RecordingDependencyGroup(
                group.Id!.Value,
                group.DisplayName.Trim()))
            .ToArray();
        var draft = new ProfileDraft(
            ProfileNameBox.Text.Trim(),
            WorkspaceRootBox.Text,
            string.IsNullOrWhiteSpace(DownloadRootBox.Text) ? null : DownloadRootBox.Text,
            selections,
            recordingDependencyGroups: recordingDependencyGroups);

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
                storedVideos,
                recordingDependencyGroups
                    .Select(group => new StoredRecordingDependencyGroup(group.Id, group.DisplayName))
                    .ToArray());

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
                ? "Profile changes saved. Media readiness was recalculated from the active selections and their persisted validation state."
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
                var hasFreshActiveProcessingJob = processingJobs.Any(job =>
                    job.State is ProcessingJobState.Queued or ProcessingJobState.Running
                    && job.UpdatedAtUtc >= staleBeforeUtc);
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
                var preprocessingResults = await profileStore.GetMediaPreprocessingResultsAsync(
                    locator.ProfileId);
                var preprocessingReconciliation = await _mediaPreprocessingService
                    .ReconcilePendingPromotionsAsync(
                        layout,
                        preprocessingResults.ToDictionary(
                            result => result.MediaAssetId,
                            result => result.ManifestWorkspaceRelativePath),
                        eligibleJournalJobs);
                _reconciledPromotionCount += preprocessingReconciliation.CompletedCount
                    + preprocessingReconciliation.RolledBackCount
                    + preprocessingReconciliation.ClearedCount;
                _promotionRecoveryWarningCount += preprocessingReconciliation.WarningCount;
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

                var preparedIntegrityFailures = preprocessingReconciliation
                    .IntegrityFailedAssetIds
                    .ToHashSet();
                var activePreparedAssetIds = hasFreshActiveProcessingJob
                    ? []
                    : profile.TrainingVideos
                        .Where(video => !video.IsArchived && video.MediaAssetId is not null)
                        .Select(video => video.MediaAssetId!.Value)
                        .Distinct()
                        .Where(assetId => committedAssets.Any(asset =>
                            asset.Id == assetId && asset.State == MediaAssetState.Prepared))
                        .ToHashSet();
                foreach (var result in preprocessingResults.Where(result =>
                             activePreparedAssetIds.Contains(result.MediaAssetId)))
                {
                    var verification = await _mediaPreprocessingService.VerifyPreparedAsync(
                        layout,
                        MapPreprocessingMetadata(result));
                    if (verification.State == MediaPreparedVerificationState.IntegrityMismatch)
                    {
                        preparedIntegrityFailures.Add(result.MediaAssetId);
                    }
                    else if (verification.State == MediaPreparedVerificationState.OperationalFailure)
                    {
                        throw new IOException(
                            "Prepared media could not be verified because of a temporary file-access problem.");
                    }
                }

                if (preparedIntegrityFailures.Count > 0)
                {
                    await profileStore.MarkMediaAssetsIntegrityFailedAsync(
                        profile.Id,
                        profile.UpdatedAtUtc,
                        preparedIntegrityFailures,
                        NextTimestamp(profile.UpdatedAtUtc));
                    profile = await profileStore.GetByIdAsync(locator.ProfileId)
                        ?? throw new InvalidDataException(
                            "The profile disappeared while recording prepared-media integrity failure.");
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
            var recordingDependencySummary = RecordingDependencyGroupSummaryBuilder.Create(profile);
            var summary = new ProfileSummaryViewModel(
                profile.Id,
                profile.DisplayName,
                profile.WorkspaceRoot,
                activeVideos.Count(video => video.Condition == TrainingCondition.VerifiedSincereTruth),
                activeVideos.Count(video => video.Condition == TrainingCondition.VerifiedIntentionalDeception),
                profile.TrainingVideos.Count(video => video.IsArchived),
                activeVideos.Count(video => video.MediaAssetId is null),
                profile.Readiness,
                recordingDependencySummary.ActiveAssignedGroupCount,
                recordingDependencySummary.ActiveUnassignedVideoCount,
                recordingDependencySummary.Conflicts.Count);
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
            : $"; {_recoveredProcessingJobCount} stale processing job(s) marked interrupted";
        var promotionStatus = _reconciledPromotionCount == 0
            ? string.Empty
            : $"; {_reconciledPromotionCount} interrupted media artifact promotion(s) reconciled";
        var promotionWarning = _promotionRecoveryWarningCount == 0
            ? string.Empty
            : $"; {_promotionRecoveryWarningCount} media artifact promotion journal(s) need manual inspection";
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

        foreach (var storedGroup in profile.RecordingDependencyGroups)
        {
            var group = new RecordingDependencyGroupOptionViewModel(
                storedGroup.Id,
                storedGroup.DisplayName);
            _recordingDependencyGroups.Add(group);
            _recordingDependencyGroupOptions.Add(group);
        }

        foreach (var storedVideo in profile.TrainingVideos.OrderBy(video => video.SortOrder))
        {
            CollectionFor(storedVideo.Condition).Add(new TrainingVideoItemViewModel(
                storedVideo.Id,
                storedVideo.FilePath,
                storedVideo.Condition,
                storedVideo.RecordingDateLabel,
                storedVideo.IsArchived,
                isPersisted: true,
                storedVideo.MediaAssetId,
                storedVideo.RecordingDependencyGroupId,
                _recordingDependencyGroupOptions));
        }

        UpdateRecordingDependencyGroupDraftSummary();
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
                video.MediaAssetId,
                video.RecordingDependencyGroupId))
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

            var item = new TrainingVideoItemViewModel(
                canonicalPath,
                condition,
                _recordingDependencyGroupOptions);
            CollectionFor(condition).Add(item);
        }

        UpdateRecordingDependencyGroupDraftSummary();

        StatusText.Text = skippedDuplicate
            ? "Selected MP4 files were added; duplicate paths were skipped."
            : "Selected MP4 files were added to the draft. No files were copied.";
    }

    private IEnumerable<TrainingVideoItemViewModel> AllVideos() =>
        _truthfulVideos.Concat(_deceptionVideos);

    private RecordingDependencyGroupOptionViewModel UnassignedRecordingDependencyGroup =>
        _recordingDependencyGroupOptions.First(group => group.Id is null);

    private void ResetRecordingDependencyGroups()
    {
        _recordingDependencyGroups.Clear();
        _recordingDependencyGroupOptions.Clear();
        _recordingDependencyGroupOptions.Add(new(null, "Unassigned"));
        NewRecordingDependencyGroupNameBox.Text = string.Empty;
        UpdateRecordingDependencyGroupDraftSummary();
    }

    private void UpdateRecordingDependencyGroupDraftSummary()
    {
        var activeVideos = AllVideos().Where(video => !video.IsArchived).ToArray();
        var activeAssignedGroupCount = activeVideos
            .Where(video => video.RecordingDependencyGroupId.HasValue)
            .Select(video => video.RecordingDependencyGroupId!.Value)
            .Distinct()
            .Count();
        var activeUnassignedCount = activeVideos.Count(video =>
            !video.RecordingDependencyGroupId.HasValue);
        var sharedAssetConflictCount = activeVideos
            .Where(video => video.MediaAssetId.HasValue
                && video.RecordingDependencyGroupId.HasValue)
            .GroupBy(video => video.MediaAssetId!.Value)
            .Count(group => group
                .Select(video => video.RecordingDependencyGroupId!.Value)
                .Distinct()
                .Skip(1)
                .Any());
        var conflicts = sharedAssetConflictCount == 0
            ? string.Empty
            : $" · {sharedAssetConflictCount} shared-asset group conflict(s); resolve before future training";

        RecordingDependencyGroupSummaryText.Text =
            $"{activeAssignedGroupCount} active recording dependency group(s) · "
            + $"{activeUnassignedCount} active Unassigned selection(s)"
            + conflicts;
    }

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
        ResetRecordingDependencyGroups();
        HideValidation();
    }

    private void ShowMainView()
    {
        ResetPreparedMediaReviewState(showMainView: false);
        ResetProcessingHistoryState(showMainView: false);
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
            && selected?.CanProcessData == true;
        ReviewPreparedMediaButton.IsEnabled = _profileStorageReady
            && !processingInThisWindow
            && !_preparedMediaReviewIsOpen
            && !_processingHistoryIsOpen
            && CanReviewPreparedMedia(selected?.Readiness);
        ProcessingHistoryButton.IsEnabled = _profileStorageReady
            && !processingInThisWindow
            && !_preparedMediaReviewIsOpen
            && !_processingHistoryIsOpen
            && selected is not null;
        CancelProcessingButton.IsEnabled = processingInThisWindow && _processingCanBeCancelled;
        RefreshProfilesButton.IsEnabled = _profileStorageReady && !processingInThisWindow;
    }

    private static bool IsProcessingReadiness(string readiness) =>
        readiness == ProfileReadiness.IngestingMedia.ToString()
        || readiness == ProfileReadiness.ValidatingMedia.ToString()
        || readiness == ProfileReadiness.PreprocessingMedia.ToString()
        || readiness == ProfileReadiness.ExtractingAudioObservations.ToString();

    private static bool CanReviewPreparedMedia(string? readiness) =>
        readiness is nameof(ProfileReadiness.MediaPrepared)
            or nameof(ProfileReadiness.AudioObservationFailed)
            or nameof(ProfileReadiness.AudioObserved);

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

    private sealed class RegisteredMediaIntegrityException(Guid mediaAssetId) : Exception
    {
        public Guid MediaAssetId { get; } = mediaAssetId;
    }
}
