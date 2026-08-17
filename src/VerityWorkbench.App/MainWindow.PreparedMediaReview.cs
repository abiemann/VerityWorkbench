using System.Collections.ObjectModel;
using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VerityWorkbench.App.ViewModels;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Core.Workspaces;
using VerityWorkbench.Data.Profiles;
using VerityWorkbench.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace VerityWorkbench.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<PreparedMediaReviewItemViewModel>
        _preparedMediaReviewItems = [];
    private readonly Dictionary<Guid, PreparedMediaReviewSource> _preparedMediaReviewSources = [];
    private CancellationTokenSource? _preparedMediaReviewLoadCancellation;
    private CancellationTokenSource? _preparedMediaSelectionCancellation;
    private SqliteProfileStore? _preparedMediaReviewStore;
    private ProfileWorkspaceLayout? _preparedMediaReviewLayout;
    private MediaPlayer? _preparedMediaPlayer;
    private MediaSource? _preparedMediaSource;
    private IRandomAccessStream? _preparedMediaRandomAccessStream;
    private PreparedMediaProxyLease? _preparedMediaProxyLease;
    private PreparedMediaReviewItemViewModel? _activePreparedMediaReviewItem;
    private Guid? _preparedMediaReviewProfileId;
    private bool _preparedMediaReviewIsOpen;

    private void InitializePreparedMediaReview() =>
        PreparedMediaReviewList.ItemsSource = _preparedMediaReviewItems;

    private async void ReviewPreparedMedia_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProfileSummaryViewModel selected)
        {
            StatusText.Text = "Select a saved profile before reviewing prepared media.";
            return;
        }

        if (!CanReviewPreparedMedia(selected.Readiness))
        {
            StatusText.Text = "Prepared-media review is available only after every active asset has completed deterministic preprocessing.";
            return;
        }

        if (_activeProcessingCancellation is not null)
        {
            StatusText.Text = "Wait for the active processing job to finish before reviewing prepared media.";
            return;
        }

        ResetPreparedMediaReviewState(showMainView: false);
        _preparedMediaReviewIsOpen = true;
        _preparedMediaReviewProfileId = selected.Id;
        PreparedMediaReviewProfileText.Text = selected.DisplayName;
        PreparedMediaReviewStatusText.Text =
            "Loading the active prepared-media inventory. Each original and prepared bundle is verified before playback.";
        PreparedMediaReviewProgress.IsActive = true;
        PreparedMediaReviewList.IsEnabled = false;
        MainView.Visibility = Visibility.Collapsed;
        AddProfileView.Visibility = Visibility.Collapsed;
        PreparedMediaReviewView.Visibility = Visibility.Visible;
        UpdateProfileActionButtons();

        var loadCancellation = new CancellationTokenSource();
        _preparedMediaReviewLoadCancellation = loadCancellation;
        try
        {
            var profileStore = CreateProfileStore(selected.WorkspaceRoot);
            var profile = await profileStore.GetByIdAsync(selected.Id, loadCancellation.Token)
                ?? throw new KeyNotFoundException("The selected profile no longer exists.");
            if (!CanReviewPreparedMedia(profile.Readiness))
            {
                throw new InvalidOperationException(
                    "The selected profile is no longer ready for prepared-media review.");
            }

            var layout = ProfileWorkspaceLayout.Create(
                profile.WorkspaceRoot,
                profile.DownloadStagingRoot);
            var assetsById = (await profileStore.GetMediaAssetsAsync(
                    profile.Id,
                    loadCancellation.Token))
                .ToDictionary(asset => asset.Id);
            var resultsByAssetId = (await profileStore.GetMediaPreprocessingResultsAsync(
                    profile.Id,
                    loadCancellation.Token))
                .ToDictionary(result => result.MediaAssetId);
            var activeGroups = profile.TrainingVideos
                .Where(video => !video.IsArchived && video.MediaAssetId is not null)
                .GroupBy(video => video.MediaAssetId!.Value)
                .OrderBy(group => group.Min(video => video.SortOrder))
                .ThenBy(group => group.Key)
                .ToArray();
            if (activeGroups.Length == 0)
            {
                throw new InvalidDataException(
                    "The prepared profile has no active registered training media.");
            }

            var durableFailureIds = new HashSet<Guid>();
            var reviewItems = new List<PreparedMediaReviewItemViewModel>(activeGroups.Length);
            var reviewSources = new List<PreparedMediaReviewSource>(activeGroups.Length);
            foreach (var group in activeGroups)
            {
                loadCancellation.Token.ThrowIfCancellationRequested();
                if (!assetsById.TryGetValue(group.Key, out var asset))
                {
                    throw new InvalidDataException(
                        "An active training selection has no registered media metadata.");
                }

                if (!resultsByAssetId.TryGetValue(group.Key, out var prepared))
                {
                    durableFailureIds.Add(group.Key);
                    continue;
                }

                if (asset.State != MediaAssetState.Prepared)
                {
                    throw new InvalidDataException(
                        "An active training asset is not in the prepared state.");
                }

                if (!string.Equals(asset.Sha256, prepared.SourceSha256, StringComparison.Ordinal)
                    || asset.ByteLength != prepared.SourceByteLength)
                {
                    durableFailureIds.Add(group.Key);
                    continue;
                }

                var conditions = group
                    .Select(video => video.Condition)
                    .Distinct()
                    .ToArray();
                if (conditions.Length != 1)
                {
                    throw new InvalidDataException(
                        "One prepared media asset is assigned to conflicting training conditions.");
                }

                var orderedSelections = group
                    .OrderBy(video => video.SortOrder)
                    .ThenBy(video => video.Id)
                    .ToArray();
                var labels = orderedSelections
                    .Select(video => string.IsNullOrWhiteSpace(video.RecordingDateLabel)
                        ? "(no recording label)"
                        : video.RecordingDateLabel)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var item = new PreparedMediaReviewItemViewModel(
                    asset.Id,
                    conditions[0],
                    labels,
                    orderedSelections.Length,
                    prepared.ProxyDurationMicroseconds,
                    prepared.SourceTimelineOriginMicroseconds);
                reviewItems.Add(item);
                reviewSources.Add(new(item, asset, prepared));
            }

            if (durableFailureIds.Count > 0)
            {
                loadCancellation.Token.ThrowIfCancellationRequested();
                await RecordPreparedMediaReviewIntegrityFailureAsync(
                    profileStore,
                    profile.Id,
                    durableFailureIds,
                    "Prepared-media metadata is missing or no longer matches its registered source asset.");
                return;
            }

            loadCancellation.Token.ThrowIfCancellationRequested();
            if (!_preparedMediaReviewIsOpen || _preparedMediaReviewProfileId != profile.Id)
            {
                return;
            }

            _preparedMediaReviewStore = profileStore;
            _preparedMediaReviewLayout = layout;
            _preparedMediaReviewSources.Clear();
            foreach (var source in reviewSources)
            {
                _preparedMediaReviewSources.Add(source.Item.MediaAssetId, source);
            }

            _preparedMediaReviewItems.Clear();
            foreach (var item in reviewItems)
            {
                _preparedMediaReviewItems.Add(item);
            }

            PreparedMediaReviewList.IsEnabled = true;
            PreparedMediaReviewStatusText.Text =
                "Select an asset to verify its original and prepared bundle, then load the presentation-only proxy.";
            if (_preparedMediaReviewItems.Count > 0)
            {
                PreparedMediaReviewList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            if (_preparedMediaReviewIsOpen)
            {
                PreparedMediaReviewStatusText.Text =
                    "Prepared-media review loading was cancelled. No profile or artifact was changed.";
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or ArithmeticException
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or KeyNotFoundException
                or DbException
                or FormatException)
        {
            if (_preparedMediaReviewIsOpen)
            {
                PreparedMediaReviewStatusText.Text =
                    "Prepared media could not be loaded. No profile or artifact was changed.";
                StatusText.Text =
                    "Prepared-media review could not be opened because its local metadata or workspace is unavailable.";
            }
        }
        finally
        {
            if (ReferenceEquals(_preparedMediaReviewLoadCancellation, loadCancellation))
            {
                _preparedMediaReviewLoadCancellation = null;
                if (_preparedMediaReviewIsOpen
                    && _preparedMediaSelectionCancellation is null)
                {
                    PreparedMediaReviewProgress.IsActive = false;
                }
            }

            loadCancellation.Dispose();
        }
    }

    private async void PreparedMediaReviewList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        StopPreparedMediaPlayer();
        ResetPreparedMediaTimestamps();
        CancelPreparedMediaSelection();
        if (!_preparedMediaReviewIsOpen
            || PreparedMediaReviewList.SelectedItem is not PreparedMediaReviewItemViewModel item
            || !_preparedMediaReviewSources.TryGetValue(item.MediaAssetId, out var source)
            || _preparedMediaReviewStore is null
            || _preparedMediaReviewLayout is null
            || _preparedMediaReviewProfileId is null)
        {
            return;
        }

        var selectionCancellation = new CancellationTokenSource();
        _preparedMediaSelectionCancellation = selectionCancellation;
        PreparedMediaReviewProgress.IsActive = true;
        PreparedMediaReviewStatusText.Text =
            "Verifying the registered original and complete prepared bundle before playback…";
        PreparedMediaProxyLease? openedProxyLease = null;
        try
        {
            var originalVerification = await _localMediaStagingService.VerifyExistingAssetStateAsync(
                _preparedMediaReviewLayout,
                source.Asset.Id,
                source.Asset.WorkspaceRelativePath,
                source.Asset.Sha256,
                source.Asset.ByteLength,
                selectionCancellation.Token);
            selectionCancellation.Token.ThrowIfCancellationRequested();
            if (originalVerification.State == LocalMediaAssetVerificationState.IntegrityMismatch)
            {
                await RecordPreparedMediaReviewIntegrityFailureAsync(
                    _preparedMediaReviewStore,
                    _preparedMediaReviewProfileId.Value,
                    [item.MediaAssetId],
                    "The registered original is missing, unsafe, or no longer matches its immutable integrity metadata.");
                return;
            }

            if (originalVerification.State == LocalMediaAssetVerificationState.OperationalFailure)
            {
                PreparedMediaReviewStatusText.Text =
                    "The registered original is temporarily unavailable, possibly because another program has it open. No integrity state was changed.";
                return;
            }

            var preparedVerification = await _mediaPreprocessingService.OpenVerifiedProxyAsync(
                _preparedMediaReviewLayout,
                MapPreprocessingMetadata(source.Prepared),
                selectionCancellation.Token);
            openedProxyLease = preparedVerification.Lease;
            selectionCancellation.Token.ThrowIfCancellationRequested();
            if (preparedVerification.State == MediaPreparedVerificationState.IntegrityMismatch)
            {
                await RecordPreparedMediaReviewIntegrityFailureAsync(
                    _preparedMediaReviewStore,
                    _preparedMediaReviewProfileId.Value,
                    [item.MediaAssetId],
                    "The prepared bundle is missing, unsafe, or no longer matches its immutable integrity metadata.");
                return;
            }

            if (preparedVerification.State == MediaPreparedVerificationState.OperationalFailure)
            {
                PreparedMediaReviewStatusText.Text =
                    "The prepared bundle is temporarily unavailable, possibly because another program has it open. No integrity state was changed.";
                return;
            }

            selectionCancellation.Token.ThrowIfCancellationRequested();
            if (!_preparedMediaReviewIsOpen
                || !ReferenceEquals(PreparedMediaReviewList.SelectedItem, item))
            {
                return;
            }

            if (openedProxyLease is null)
            {
                throw new InvalidDataException(
                    "Prepared-media verification did not return a playback lease.");
            }

            StartPreparedMediaPlayer(item, openedProxyLease);
            openedProxyLease = null;
            PreparedMediaReviewStatusText.Text =
                "The original and complete prepared bundle match their recorded SHA-256 metadata. The proxy is loaded for presentation-only review; no analysis or scoring was performed.";
            StatusText.Text =
                "Reviewing a verified presentation-only proxy. Media quality and model applicability remain not assessed.";
        }
        catch (OperationCanceledException)
        {
            // A newer selection, navigation, or window close superseded this verification.
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or COMException)
        {
            if (_preparedMediaReviewIsOpen
                && ReferenceEquals(PreparedMediaReviewList.SelectedItem, item))
            {
                PreparedMediaReviewStatusText.Text =
                    "The verified playback proxy could not be opened because of a temporary media or file-access problem. No integrity state was changed.";
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidDataException
                or InvalidOperationException
                or FormatException)
        {
            if (_preparedMediaReviewIsOpen
                && ReferenceEquals(PreparedMediaReviewList.SelectedItem, item))
            {
                PreparedMediaReviewStatusText.Text =
                    "The prepared-media record cannot be used for playback. No proxy was opened.";
                StatusText.Text =
                    "Prepared-media review failed because its stored metadata is invalid. No proxy was opened.";
            }
        }
        finally
        {
            openedProxyLease?.Dispose();
            if (ReferenceEquals(_preparedMediaSelectionCancellation, selectionCancellation))
            {
                _preparedMediaSelectionCancellation = null;
                if (_preparedMediaReviewIsOpen)
                {
                    PreparedMediaReviewProgress.IsActive = false;
                }
            }

            selectionCancellation.Dispose();
        }
    }

    private void ClosePreparedMediaReview_Click(object sender, RoutedEventArgs e)
    {
        ResetPreparedMediaReviewState(showMainView: true);
        StatusText.Text =
            "Prepared-media review closed. No profile, media, analysis, or scoring state was changed.";
    }

    private async Task RecordPreparedMediaReviewIntegrityFailureAsync(
        SqliteProfileStore profileStore,
        Guid profileId,
        IReadOnlyCollection<Guid> mediaAssetIds,
        string reason)
    {
        StopPreparedMediaPlayer();
        PreparedMediaReviewList.IsEnabled = false;
        var recorded = await TryPersistMediaIntegrityFailureAsync(
            profileStore,
            profileId,
            mediaAssetIds);
        string? refreshWarning = null;
        if (recorded
            && _preparedMediaReviewIsOpen
            && _preparedMediaReviewProfileId == profileId)
        {
            refreshWarning = await TryReloadProfilesAfterProcessingAsync(profileId);
        }

        if (!_preparedMediaReviewIsOpen || _preparedMediaReviewProfileId != profileId)
        {
            return;
        }

        PreparedMediaReviewStatusText.Text = recorded
            ? reason + " The profile is marked as needing repair and playback is blocked."
            : reason + " The repair-required state could not be saved; playback remains blocked.";
        StatusText.Text = recorded
            ? "Prepared-media integrity verification failed. The profile is marked as needing repair."
            : "Prepared-media integrity verification failed, but the repair-required state could not be saved. Refresh the profile and do not continue processing it.";
        if (refreshWarning is not null)
        {
            StatusText.Text += " The profile list could not be refreshed.";
        }
    }

    private void StartPreparedMediaPlayer(
        PreparedMediaReviewItemViewModel item,
        PreparedMediaProxyLease proxyLease)
    {
        StopPreparedMediaPlayer();
        IRandomAccessStream? randomAccessStream = null;
        MediaSource? mediaSource = null;
        MediaPlayer? mediaPlayer = null;
        try
        {
            randomAccessStream = proxyLease.Stream.AsRandomAccessStream();
            mediaSource = MediaSource.CreateFromStream(randomAccessStream, "video/mp4");
            mediaPlayer = new MediaPlayer
            {
                AutoPlay = false,
                Volume = 0.5,
            };
        }
        catch
        {
            mediaPlayer?.Dispose();
            mediaSource?.Dispose();
            randomAccessStream?.Dispose();
            proxyLease.Dispose();
            throw;
        }

        mediaPlayer.PlaybackSession.PositionChanged += PreparedMediaPlaybackSession_PositionChanged;
        mediaPlayer.MediaFailed += PreparedMediaPlayer_MediaFailed;
        _preparedMediaProxyLease = proxyLease;
        _preparedMediaRandomAccessStream = randomAccessStream;
        _preparedMediaSource = mediaSource;
        _preparedMediaPlayer = mediaPlayer;
        _activePreparedMediaReviewItem = item;
        try
        {
            PreparedMediaPlayerElement.SetMediaPlayer(mediaPlayer);
            mediaPlayer.Source = mediaSource;
            UpdatePreparedMediaTimestamps(TimeSpan.Zero, item);
        }
        catch
        {
            StopPreparedMediaPlayer();
            throw;
        }
    }

    private void PreparedMediaPlaybackSession_PositionChanged(
        MediaPlaybackSession sender,
        object args)
    {
        var position = sender.Position;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_preparedMediaReviewIsOpen
                && _preparedMediaPlayer?.PlaybackSession == sender
                && _activePreparedMediaReviewItem is { } item)
            {
                UpdatePreparedMediaTimestamps(position, item);
            }
        });
    }

    private void PreparedMediaPlayer_MediaFailed(
        MediaPlayer sender,
        MediaPlayerFailedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_preparedMediaReviewIsOpen && ReferenceEquals(_preparedMediaPlayer, sender))
            {
                PreparedMediaReviewStatusText.Text =
                    "Windows could not decode the verified presentation proxy. No integrity, analysis, or scoring state was changed.";
                StopPreparedMediaPlayer();
                ResetPreparedMediaTimestamps();
            }
        });

    private void UpdatePreparedMediaTimestamps(
        TimeSpan position,
        PreparedMediaReviewItemViewModel item)
    {
        var targetMicroseconds = Math.Clamp(
            position.Ticks / 10,
            0,
            item.DurationMicroseconds);
        var sourceMicroseconds = AddTimelineMicroseconds(
            item.SourceTimelineOriginMicroseconds,
            targetMicroseconds);
        PreparedMediaTargetTimestampText.Text = FormatTimelineMicroseconds(targetMicroseconds);
        PreparedMediaSourceTimestampText.Text = FormatTimelineMicroseconds(sourceMicroseconds);
    }

    private static long AddTimelineMicroseconds(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        if (right < 0 && left < long.MinValue - right)
        {
            return long.MinValue;
        }

        return left + right;
    }

    private static string FormatTimelineMicroseconds(long microseconds)
    {
        var isNegative = microseconds < 0;
        var absolute = isNegative
            ? (ulong)(-(microseconds + 1)) + 1
            : (ulong)microseconds;
        var totalMilliseconds = absolute / 1_000;
        var milliseconds = totalMilliseconds % 1_000;
        var totalSeconds = totalMilliseconds / 1_000;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var minutes = totalMinutes % 60;
        var hours = totalMinutes / 60;
        return $"{(isNegative ? "-" : string.Empty)}{hours:00}:{minutes:00}:{seconds:00}.{milliseconds:000}";
    }

    private void ResetPreparedMediaTimestamps()
    {
        PreparedMediaTargetTimestampText.Text = "00:00:00.000";
        PreparedMediaSourceTimestampText.Text = "—";
    }

    private void StopPreparedMediaPlayer()
    {
        if (_preparedMediaPlayer is not null)
        {
            _preparedMediaPlayer.PlaybackSession.PositionChanged -=
                PreparedMediaPlaybackSession_PositionChanged;
            _preparedMediaPlayer.MediaFailed -= PreparedMediaPlayer_MediaFailed;
            _preparedMediaPlayer.Pause();
            _preparedMediaPlayer.Source = null;
        }

        PreparedMediaPlayerElement.SetMediaPlayer(null);
        _preparedMediaPlayer?.Dispose();
        _preparedMediaSource?.Dispose();
        _preparedMediaRandomAccessStream?.Dispose();
        _preparedMediaProxyLease?.Dispose();
        _preparedMediaPlayer = null;
        _preparedMediaSource = null;
        _preparedMediaRandomAccessStream = null;
        _preparedMediaProxyLease = null;
        _activePreparedMediaReviewItem = null;
    }

    private void CancelPreparedMediaSelection()
    {
        var cancellation = _preparedMediaSelectionCancellation;
        _preparedMediaSelectionCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
    }

    private void ResetPreparedMediaReviewState(bool showMainView)
    {
        _preparedMediaReviewIsOpen = false;
        var loadCancellation = _preparedMediaReviewLoadCancellation;
        _preparedMediaReviewLoadCancellation = null;
        if (loadCancellation is not null)
        {
            loadCancellation.Cancel();
        }

        CancelPreparedMediaSelection();
        StopPreparedMediaPlayer();
        PreparedMediaReviewList.SelectedItem = null;
        PreparedMediaReviewList.IsEnabled = false;
        PreparedMediaReviewProgress.IsActive = false;
        _preparedMediaReviewItems.Clear();
        _preparedMediaReviewSources.Clear();
        _preparedMediaReviewStore = null;
        _preparedMediaReviewLayout = null;
        _preparedMediaReviewProfileId = null;
        ResetPreparedMediaTimestamps();
        PreparedMediaReviewView.Visibility = Visibility.Collapsed;
        if (showMainView)
        {
            MainView.Visibility = Visibility.Visible;
            UpdateProfileActionButtons();
        }
    }

    private void DisposePreparedMediaReview() =>
        ResetPreparedMediaReviewState(showMainView: false);

    private sealed record PreparedMediaReviewSource(
        PreparedMediaReviewItemViewModel Item,
        StoredMediaAsset Asset,
        StoredMediaPreprocessingResult Prepared);
}
