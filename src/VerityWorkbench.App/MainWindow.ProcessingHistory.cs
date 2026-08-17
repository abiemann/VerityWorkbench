using System.Collections.ObjectModel;
using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VerityWorkbench.App.ViewModels;
using VerityWorkbench.Core.Workspaces;
using VerityWorkbench.Data.Profiles;
using VerityWorkbench.Media;
using Windows.Storage;
using Windows.System;

namespace VerityWorkbench.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<ProcessingHistoryItemViewModel>
        _processingHistoryItems = [];
    private readonly ProcessingJobDirectoryService _processingJobDirectoryService = new();
    private CancellationTokenSource? _processingHistoryLoadCancellation;
    private CancellationTokenSource? _processingHistoryOperationCancellation;
    private SqliteProfileStore? _processingHistoryStore;
    private ProfileWorkspaceLayout? _processingHistoryLayout;
    private Guid? _processingHistoryProfileId;
    private bool _processingHistoryIsOpen;
    private bool _processingHistoryOperationInProgress;

    private void InitializeProcessingHistory() =>
        ProcessingHistoryList.ItemsSource = _processingHistoryItems;

    private async void ProcessingHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not ProfileSummaryViewModel selected)
        {
            StatusText.Text = "Select a saved profile before opening Processing History.";
            return;
        }

        if (_activeProcessingCancellation is not null)
        {
            StatusText.Text =
                "Wait for the processing job in this app window to finish before opening Processing History.";
            return;
        }

        ResetPreparedMediaReviewState(showMainView: false);
        ResetProcessingHistoryState(showMainView: false);
        _processingHistoryIsOpen = true;
        _processingHistoryProfileId = selected.Id;
        ProcessingHistoryProfileText.Text = selected.DisplayName;
        ProcessingHistoryProgress.IsActive = true;
        ProcessingHistoryList.IsEnabled = false;
        EmptyProcessingHistoryText.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Collapsed;
        AddProfileView.Visibility = Visibility.Collapsed;
        PreparedMediaReviewView.Visibility = Visibility.Collapsed;
        ProcessingHistoryView.Visibility = Visibility.Visible;
        StatusText.Text = "Loading the selected profile's processing-job history…";
        UpdateProfileActionButtons();

        var loadCancellation = new CancellationTokenSource();
        _processingHistoryLoadCancellation = loadCancellation;
        try
        {
            var profileStore = CreateProfileStore(selected.WorkspaceRoot);
            var profile = await profileStore.GetByIdAsync(selected.Id, loadCancellation.Token)
                ?? throw new KeyNotFoundException("The selected profile no longer exists.");
            if (!string.Equals(
                    Path.GetFullPath(profile.WorkspaceRoot),
                    Path.GetFullPath(selected.WorkspaceRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The selected profile workspace no longer matches the loaded profile.");
            }

            var layout = ProfileWorkspaceLayout.Create(
                profile.WorkspaceRoot,
                profile.DownloadStagingRoot);
            var jobs = await profileStore.GetProcessingJobsAsync(
                profile.Id,
                loadCancellation.Token);
            loadCancellation.Token.ThrowIfCancellationRequested();
            if (!_processingHistoryIsOpen || _processingHistoryProfileId != profile.Id)
            {
                return;
            }

            _processingHistoryStore = profileStore;
            _processingHistoryLayout = layout;
            ReplaceProcessingHistoryItems(jobs);
            ProcessingHistoryList.IsEnabled = true;
            StatusText.Text = jobs.Count == 0
                ? "This profile has no recorded processing jobs."
                : "Processing History loaded. Opening or deleting a folder rechecks its current database and workspace state.";
        }
        catch (OperationCanceledException)
        {
            // Navigation or window close superseded this load.
        }
        catch (Exception exception) when (IsExpectedProcessingHistoryException(exception))
        {
            if (_processingHistoryIsOpen)
            {
                StatusText.Text =
                    "Processing History could not be loaded because the selected profile metadata or workspace is unavailable.";
                EmptyProcessingHistoryText.Text = "Processing History is unavailable.";
                EmptyProcessingHistoryText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (ReferenceEquals(_processingHistoryLoadCancellation, loadCancellation))
            {
                _processingHistoryLoadCancellation = null;
                if (_processingHistoryIsOpen)
                {
                    ProcessingHistoryProgress.IsActive = false;
                }
            }

            loadCancellation.Dispose();
        }
    }

    private async void OpenProcessingJobFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProcessingHistoryItemViewModel item }
            || !TryBeginProcessingHistoryOperation(out var operationCancellation))
        {
            return;
        }

        var profileStore = _processingHistoryStore!;
        var layout = _processingHistoryLayout!;
        var profileId = _processingHistoryProfileId!.Value;
        try
        {
            var freshJob = await ReadMatchingProcessingJobAsync(
                item,
                profileStore,
                layout,
                profileId,
                operationCancellation.Token);
            if (freshJob is null)
            {
                return;
            }

            var inspection = _processingJobDirectoryService.Inspect(
                layout,
                freshJob.Id,
                freshJob.WorkspaceRelativePath);
            if (inspection.State == ProcessingJobDirectoryState.Missing
                || string.IsNullOrWhiteSpace(inspection.FullPath))
            {
                if (IsProcessingHistoryContextCurrent(profileId))
                {
                    item.SetFolderNotice("Processing folder missing — no cleanup state changed");
                    StatusText.Text =
                        "The recorded processing folder is missing. It was not marked as deleted; inspect the workspace before taking further action.";
                }

                return;
            }

            operationCancellation.Token.ThrowIfCancellationRequested();
            var folder = await StorageFolder.GetFolderFromPathAsync(inspection.FullPath);
            operationCancellation.Token.ThrowIfCancellationRequested();
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                if (IsProcessingHistoryContextCurrent(profileId))
                {
                    StatusText.Text =
                        "Windows could not open the verified processing folder. No profile or cleanup state changed.";
                }

                return;
            }

            if (IsProcessingHistoryContextCurrent(profileId))
            {
                item.SetFolderNotice("Retained processing folder · opened after safety verification");
                StatusText.Text =
                    "Opened the verified processing-job folder. No profile, media, result, readiness, or cleanup state changed.";
            }
        }
        catch (OperationCanceledException)
        {
            // Navigation or window close superseded this open request.
        }
        catch (ProcessingJobDirectoryException exception)
        {
            if (IsProcessingHistoryContextCurrent(profileId))
            {
                ShowProcessingJobDirectoryRefusal(item, exception.Failure, deleting: false);
            }
        }
        catch (Exception exception) when (IsExpectedProcessingHistoryException(exception))
        {
            if (IsProcessingHistoryContextCurrent(profileId))
            {
                item.SetFolderNotice("Processing folder temporarily unavailable");
                StatusText.Text =
                    "The processing folder could not be opened because of a local file or metadata problem. No cleanup state changed.";
            }
        }
        finally
        {
            EndProcessingHistoryOperation(operationCancellation);
        }
    }

    private async void DeleteProcessingJobData_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ProcessingHistoryItemViewModel item }
            || !_processingHistoryIsOpen
            || _processingHistoryOperationInProgress)
        {
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = ProcessingHistoryView.XamlRoot,
            Title = "Delete retained processing data?",
            Content =
                $"This permanently deletes only the bounded folder '{item.WorkspaceRelativePath}'. "
                + "The processing-job audit record, registered media, prepared bundles, persisted results, and profile readiness remain unchanged.",
            PrimaryButtonText = "Delete Processing Data",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary
            || !TryBeginProcessingHistoryOperation(out var operationCancellation))
        {
            return;
        }

        var profileStore = _processingHistoryStore!;
        var layout = _processingHistoryLayout!;
        var profileId = _processingHistoryProfileId!.Value;
        var processingFolderDeleted = false;
        try
        {
            var freshJob = await ReadMatchingProcessingJobAsync(
                item,
                profileStore,
                layout,
                profileId,
                operationCancellation.Token);
            if (freshJob is null)
            {
                return;
            }

            if (!IsTerminalProcessingJobState(freshJob.State))
            {
                if (IsProcessingHistoryContextCurrent(profileId))
                {
                    StatusText.Text =
                        "The processing job is active and its folder cannot be deleted. Refresh Processing History after the job stops.";
                }

                return;
            }

            var inspection = _processingJobDirectoryService.Inspect(
                layout,
                freshJob.Id,
                freshJob.WorkspaceRelativePath);
            if (inspection.State == ProcessingJobDirectoryState.Missing)
            {
                if (IsProcessingHistoryContextCurrent(profileId))
                {
                    item.SetFolderNotice("Processing folder missing — no cleanup state changed");
                    StatusText.Text =
                        "The recorded processing folder is missing. It was not marked as deleted; inspect the workspace before taking further action.";
                }

                return;
            }

            await _processingJobDirectoryService.DeleteAsync(
                layout,
                freshJob.Id,
                freshJob.WorkspaceRelativePath,
                operationCancellation.Token);
            processingFolderDeleted = true;

            var cleanedAtUtc = DateTimeOffset.UtcNow;
            if (cleanedAtUtc <= freshJob.UpdatedAtUtc)
            {
                cleanedAtUtc = freshJob.UpdatedAtUtc.AddTicks(1);
            }

            var cleanupRecorded = await profileStore.MarkProcessingJobWorkspaceCleanedAsync(
                    freshJob.ProfileId,
                    freshJob.Id,
                    freshJob.State,
                    freshJob.WorkspaceRelativePath,
                    cleanedAtUtc,
                    CancellationToken.None);
            if (!cleanupRecorded)
            {
                var currentJob = await profileStore.GetProcessingJobAsync(
                    freshJob.Id,
                    CancellationToken.None);
                if (currentJob?.WorkspaceCleanedAtUtc is { } alreadyCleanedAtUtc
                    && currentJob.ProfileId == freshJob.ProfileId
                    && currentJob.State == freshJob.State
                    && string.Equals(
                        currentJob.WorkspaceRelativePath,
                        freshJob.WorkspaceRelativePath,
                        StringComparison.Ordinal))
                {
                    if (IsProcessingHistoryContextCurrent(profileId))
                    {
                        item.MarkWorkspaceCleaned(alreadyCleanedAtUtc);
                        StatusText.Text =
                            "The retained processing folder was already deleted and its cleanup record is current.";
                    }

                    return;
                }

                if (IsProcessingHistoryContextCurrent(profileId))
                {
                    item.SetFolderNotice("Folder deleted · cleanup audit update needs attention");
                    StatusText.Text =
                        "The retained processing folder was deleted, but its cleanup timestamp could not be recorded. Refresh and inspect Processing History before retrying.";
                }

                return;
            }

            if (IsProcessingHistoryContextCurrent(profileId))
            {
                item.MarkWorkspaceCleaned(cleanedAtUtc);
                StatusText.Text =
                    "Deleted the selected retained processing folder. Its audit record, profile readiness, media, prepared bundles, and persisted results were preserved.";
            }
        }
        catch (OperationCanceledException)
        {
            if (processingFolderDeleted && IsProcessingHistoryContextCurrent(profileId))
            {
                item.SetFolderNotice("Folder deleted · cleanup audit update needs attention");
                StatusText.Text =
                    "The processing folder was deleted, but its cleanup timestamp could not be confirmed. Refresh and inspect Processing History before retrying.";
            }
        }
        catch (ProcessingJobDirectoryException exception)
        {
            if (IsProcessingHistoryContextCurrent(profileId))
            {
                if (processingFolderDeleted)
                {
                    item.SetFolderNotice("Folder deleted · cleanup audit update needs attention");
                    StatusText.Text =
                        "The processing folder was deleted, but its cleanup timestamp could not be recorded. Refresh and inspect Processing History before retrying.";
                }
                else
                {
                    ShowProcessingJobDirectoryRefusal(item, exception.Failure, deleting: true);
                }
            }
        }
        catch (Exception exception) when (IsExpectedProcessingHistoryException(exception))
        {
            if (IsProcessingHistoryContextCurrent(profileId))
            {
                if (processingFolderDeleted)
                {
                    item.SetFolderNotice("Folder deleted · cleanup audit update needs attention");
                    StatusText.Text =
                        "The processing folder was deleted, but its cleanup timestamp could not be recorded. Refresh and inspect Processing History before retrying.";
                }
                else
                {
                    item.SetFolderNotice("Deletion did not complete · cleanup state unchanged");
                    StatusText.Text =
                        "The processing folder could not be fully deleted, possibly because another program is using it. No cleanup state was recorded; close the other program and retry.";
                }
            }
        }
        finally
        {
            EndProcessingHistoryOperation(operationCancellation);
        }
    }

    private async Task<StoredProcessingJob?> ReadMatchingProcessingJobAsync(
        ProcessingHistoryItemViewModel item,
        SqliteProfileStore profileStore,
        ProfileWorkspaceLayout layout,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var freshProfile = await profileStore.GetByIdAsync(profileId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (freshProfile is null
            || freshProfile.Id != profileId
            || !string.Equals(
                Path.GetFullPath(freshProfile.WorkspaceRoot),
                Path.GetFullPath(layout.WorkspaceRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            if (IsProcessingHistoryContextCurrent(profileId))
            {
                item.SetFolderNotice("Profile workspace changed — action refused");
                StatusText.Text =
                    "The selected profile or its workspace changed after Processing History loaded. The folder action was refused.";
            }

            return null;
        }

        var freshJob = await profileStore.GetProcessingJobAsync(
            item.Id,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (freshJob is null || freshJob.ProfileId != profileId)
        {
            if (IsProcessingHistoryContextCurrent(profileId))
            {
                item.SetFolderNotice("Job record unavailable — action refused");
                StatusText.Text =
                    "The processing-job record is no longer available for this profile. The folder action was refused.";
            }

            return null;
        }

        if (!item.Matches(freshJob))
        {
            if (IsProcessingHistoryContextCurrent(profileId))
            {
                item.SetFolderNotice("Job record changed — reopen Processing History");
                StatusText.Text =
                    "The processing-job state, path, or cleanup status changed after this view loaded. The folder action was refused; return to profiles and reopen Processing History.";
            }

            return null;
        }

        if (freshJob.WorkspaceCleanedAtUtc is not null)
        {
            if (IsProcessingHistoryContextCurrent(profileId))
            {
                item.MarkWorkspaceCleaned(freshJob.WorkspaceCleanedAtUtc.Value);
                StatusText.Text = "This processing folder is already recorded as deleted.";
            }

            return null;
        }

        return freshJob;
    }

    private bool IsProcessingHistoryContextCurrent(Guid profileId) =>
        _processingHistoryIsOpen && _processingHistoryProfileId == profileId;

    private bool TryBeginProcessingHistoryOperation(
        out CancellationTokenSource operationCancellation)
    {
        operationCancellation = new CancellationTokenSource();
        if (!_processingHistoryIsOpen
            || _processingHistoryOperationInProgress
            || _processingHistoryStore is null
            || _processingHistoryLayout is null
            || _processingHistoryProfileId is null)
        {
            operationCancellation.Dispose();
            return false;
        }

        _processingHistoryOperationInProgress = true;
        _processingHistoryOperationCancellation = operationCancellation;
        ProcessingHistoryProgress.IsActive = true;
        ProcessingHistoryList.IsEnabled = false;
        CloseProcessingHistoryButton.IsEnabled = false;
        foreach (var item in _processingHistoryItems)
        {
            item.SetBusy(true);
        }

        return true;
    }

    private void EndProcessingHistoryOperation(
        CancellationTokenSource operationCancellation)
    {
        if (ReferenceEquals(_processingHistoryOperationCancellation, operationCancellation))
        {
            _processingHistoryOperationCancellation = null;
            _processingHistoryOperationInProgress = false;
            if (_processingHistoryIsOpen)
            {
                ProcessingHistoryProgress.IsActive = false;
                ProcessingHistoryList.IsEnabled = true;
                CloseProcessingHistoryButton.IsEnabled = true;
                foreach (var item in _processingHistoryItems)
                {
                    item.SetBusy(false);
                }
            }
        }

        operationCancellation.Dispose();
    }

    private void ReplaceProcessingHistoryItems(
        IReadOnlyCollection<StoredProcessingJob> jobs)
    {
        _processingHistoryItems.Clear();
        foreach (var job in jobs
                     .OrderByDescending(job => job.CreatedAtUtc)
                     .ThenByDescending(job => job.Id))
        {
            _processingHistoryItems.Add(new ProcessingHistoryItemViewModel(job));
        }

        EmptyProcessingHistoryText.Text =
            "No processing jobs are recorded for this profile.";
        EmptyProcessingHistoryText.Visibility = _processingHistoryItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowProcessingJobDirectoryRefusal(
        ProcessingHistoryItemViewModel item,
        ProcessingJobDirectoryFailure failure,
        bool deleting)
    {
        if (!_processingHistoryIsOpen)
        {
            return;
        }

        if (failure == ProcessingJobDirectoryFailure.Missing)
        {
            item.SetFolderNotice("Processing folder missing — no cleanup state changed");
            StatusText.Text =
                "The recorded processing folder is missing. It was not marked as deleted; inspect the workspace before taking further action.";
            return;
        }

        if (failure == ProcessingJobDirectoryFailure.PendingPromotionEvidence)
        {
            item.SetFolderNotice("Pending promotion evidence — action refused");
            StatusText.Text =
                "The processing folder contains pending promotion evidence. Refresh the profile so reconciliation can finish before opening or deleting it.";
            return;
        }

        item.SetFolderNotice("Processing folder failed safety validation — action refused");
        StatusText.Text = deleting
            ? "The processing folder failed bounded-path or job-identity validation and was not deleted. No cleanup state changed."
            : "The processing folder failed bounded-path or job-identity validation and was not opened.";
    }

    private void CloseProcessingHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_processingHistoryOperationInProgress)
        {
            StatusText.Text =
                "Wait for the current Processing History folder action to finish before returning to profiles.";
            return;
        }

        ResetProcessingHistoryState(showMainView: true);
        StatusText.Text =
            "Processing History closed. No profile readiness, media, result, or scientific state changed.";
    }

    private void ResetProcessingHistoryState(bool showMainView)
    {
        _processingHistoryIsOpen = false;
        var loadCancellation = _processingHistoryLoadCancellation;
        _processingHistoryLoadCancellation = null;
        loadCancellation?.Cancel();
        var operationCancellation = _processingHistoryOperationCancellation;
        _processingHistoryOperationCancellation = null;
        operationCancellation?.Cancel();
        _processingHistoryOperationInProgress = false;
        _processingHistoryItems.Clear();
        _processingHistoryStore = null;
        _processingHistoryLayout = null;
        _processingHistoryProfileId = null;
        ProcessingHistoryProgress.IsActive = false;
        ProcessingHistoryList.IsEnabled = false;
        EmptyProcessingHistoryText.Visibility = Visibility.Collapsed;
        CloseProcessingHistoryButton.IsEnabled = true;
        ProcessingHistoryView.Visibility = Visibility.Collapsed;
        if (showMainView)
        {
            MainView.Visibility = Visibility.Visible;
            UpdateProfileActionButtons();
        }
    }

    private void DisposeProcessingHistory() =>
        ResetProcessingHistoryState(showMainView: false);

    private static bool IsTerminalProcessingJobState(ProcessingJobState state) =>
        state is ProcessingJobState.Completed
            or ProcessingJobState.Cancelled
            or ProcessingJobState.Failed
            or ProcessingJobState.Interrupted;

    private static bool IsExpectedProcessingHistoryException(Exception exception) =>
        exception is ArgumentException
            or ArithmeticException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or KeyNotFoundException
            or DbException
            or FormatException
            or COMException;
}
