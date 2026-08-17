using System.Data.Common;
using System.Globalization;
using VerityWorkbench.App.ViewModels;
using VerityWorkbench.Core.Profiles;
using VerityWorkbench.Core.Workspaces;
using VerityWorkbench.Data.Profiles;
using VerityWorkbench.Media;

namespace VerityWorkbench.App;

public sealed partial class MainWindow
{
    private async Task RunAudioObservationExtractionAsync(
        ProfileSummaryViewModel selected,
        SqliteProfileStore profileStore,
        StoredProfile profile,
        ProfileWorkspaceLayout layout,
        CancellationTokenSource processingCancellation)
    {
        _activeProcessingKind = ProcessingJobKind.AudioObservationExtraction;
        var jobId = Guid.NewGuid();
        var startedAtUtc = NextTimestamp(profile.UpdatedAtUtc);
        var relativeJobPath = BuildAudioObservationJobRelativePath(jobId, startedAtUtc);
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
            selected.SetLiveStatus("Preparing objective audio observations…");
            StatusText.Text = "Creating a label-blind snapshot of the prepared analysis-audio files…";
            var job = await profileStore.StartAudioObservationJobAsync(
                profile.Id,
                profile.UpdatedAtUtc,
                jobId,
                relativeJobPath,
                AudioPcmObservationService.CurrentObservationContractVersion,
                AudioPcmObservationService.CurrentObservationContractSha256,
                startedAtUtc,
                processingCancellation.Token);
            jobStarted = true;
            _ = CreateProcessingJobDirectory(
                layout,
                relativeJobPath,
                "audio-observation");

            if (!await profileStore.UpdateProcessingJobProgressAsync(
                    jobId,
                    ProcessingJobState.Running,
                    0,
                    0,
                    NextTimestamp(startedAtUtc),
                    processingCancellation.Token))
            {
                throw new InvalidOperationException(
                    "The audio-observation job stopped before analysis began.");
            }

            var jobAssets = await profileStore.GetAudioObservationJobAssetsAsync(
                jobId,
                processingCancellation.Token);
            if (jobAssets.Count == 0 || jobAssets.Count != job.TotalItemCount)
            {
                throw new InvalidDataException(
                    "The audio-observation job has an inconsistent media snapshot.");
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

            var registrations = new List<AudioObservationRegistration>(jobAssets.Count);
            for (var index = 0; index < jobAssets.Count; index++)
            {
                processingCancellation.Token.ThrowIfCancellationRequested();
                var snapshot = jobAssets[index];
                var itemNumber = index + 1;
                selected.SetLiveStatus(
                    $"Observing analysis audio {itemNumber}/{jobAssets.Count}");
                StatusText.Text =
                    $"Reading the complete verified PCM stream for unique prepared asset {itemNumber}/{jobAssets.Count}. " +
                    "No labels, thresholds, speech inference, or scores are used.";

                var prepared = await profileStore.GetMediaPreprocessingResultAsync(
                    snapshot.MediaAssetId,
                    processingCancellation.Token)
                    ?? throw new InvalidDataException(
                        "A snapshotted prepared asset has no immutable preprocessing result.");

                try
                {
                    var observed = await _audioPcmObservationService.ObserveAsync(
                        layout,
                        MapPreprocessingMetadata(prepared),
                        processingCancellation.Token);
                    registrations.Add(new(
                        snapshot.MediaAssetId,
                        MapAudioObservationResult(
                            observed,
                            NextTimestamp(startedAtUtc)),
                        FailureMessage: null));
                }
                catch (AudioPcmObservationException exception) when (
                    exception.Failure == AudioPcmObservationFailure.PreparedIntegrityMismatch)
                {
                    integrityFailedAssetId = snapshot.MediaAssetId;
                    throw;
                }
                catch (AudioPcmObservationException exception) when (
                    exception.Failure is AudioPcmObservationFailure.PreparedMetadataMismatch
                        or AudioPcmObservationFailure.WaveMalformed
                        or AudioPcmObservationFailure.WaveContractMismatch)
                {
                    registrations.Add(new(
                        snapshot.MediaAssetId,
                        Result: null,
                        exception.Failure.ToString()));
                }

                Interlocked.Exchange(ref latestCompletedItems, itemNumber);
                Interlocked.Add(
                    ref latestCompletedBytes,
                    snapshot.AnalysisAudioByteLength);
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
                        "The audio-observation job stopped before all results were recorded.");
                }
            }

            var heartbeatError = await StopHeartbeatAsync();
            if (heartbeatError is not null)
            {
                throw new IOException(
                    "Audio-observation progress could not be persisted.",
                    heartbeatError);
            }

            var latestObservedAtUtc = registrations
                .Where(registration => registration.Result is not null)
                .Select(registration => registration.Result!.ObservedAtUtc)
                .DefaultIfEmpty(startedAtUtc)
                .Max();
            processingCancellation.Token.ThrowIfCancellationRequested();
            _processingCanBeCancelled = false;
            CancelProcessingButton.IsEnabled = false;
            selected.SetLiveStatus("Finalizing objective audio observations…");
            StatusText.Text = "The exact PCM scan is complete. Committing its immutable, label-blind observations…";
            await profileStore.CompleteAudioObservationJobAsync(
                jobId,
                registrations,
                NextTimestamp(latestObservedAtUtc),
                CancellationToken.None);
            completionCommitted = true;

            await ReloadProfilesAsync(profile.Id);
            var failureCount = registrations.Count(registration => registration.Result is null);
            StatusText.Text = failureCount == 0
                ? $"Exact whole-file objective analysis-audio observations were recorded for {registrations.Count} unique prepared asset(s). Media quality and model applicability remain not assessed; no speech, language, behavior, truth, or deception result was produced."
                : $"Objective audio observation extraction completed, but {failureCount} asset(s) need attention. Successful observations were saved; quality and model applicability remain not assessed, and no scoring was performed.";
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
                        : "Audio observation stopped because progress persistence failed.");
            }

            var refreshWarning = jobStarted
                ? await TryReloadProfilesAfterProcessingAsync(profile.Id)
                : null;
            StatusText.Text = jobStarted
                ? "Objective audio observation extraction cancelled. The analysis-audio file was closed, no observation result was written, and the bounded Processing job folder was retained."
                : "Audio-observation preparation cancelled. No processing job or observation result was created.";
            if (!terminalStateRecorded)
            {
                StatusText.Text += " The terminal job status could not be saved; use Refresh after the ten-minute recovery grace period.";
            }

            if (refreshWarning is not null)
            {
                StatusText.Text += " The profile list could not be refreshed.";
            }
        }
        catch (Exception exception) when (IsExpectedAudioObservationWorkflowException(exception))
        {
            await StopHeartbeatAsync();
            var terminalStateRecorded = true;
            if (jobStarted && !completionCommitted)
            {
                terminalStateRecorded = await TryTerminateProcessingJobAsync(
                    profileStore,
                    jobId,
                    ProcessingJobState.Failed,
                    "Objective audio observation extraction failed before completion.");
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
                ? await TryReloadProfilesAfterProcessingAsync(profile.Id)
                : null;
            StatusText.Text = completionCommitted
                ? "Objective audio observations were saved, but the profile list could not be refreshed. Select Refresh before continuing."
                : integrityFailedAssetId is not null
                    ? integrityStateRecorded
                        ? "A prepared media bundle changed before objective audio observation. The profile is marked as needing repair; no observation result was accepted."
                        : "A prepared media bundle changed, but the repair-required state could not be saved. Refresh the profile and do not continue processing it."
                    : GetSafeAudioObservationFailureMessage(exception, jobStarted);
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

    private static StoredAudioObservationResult MapAudioObservationResult(
        AudioPcmObservationResult result,
        DateTimeOffset observedAtUtc) =>
        new(
            result.MediaAssetId,
            result.AnalysisAudioSha256,
            result.AnalysisAudioByteLength,
            result.SampleRateHz,
            result.ChannelCount,
            result.ProcessedSampleCount,
            result.DurationMicroseconds,
            result.PreprocessingContractSha256,
            result.ObservationContractVersion,
            result.ObservationContractSha256,
            result.MinimumSample,
            result.MaximumSample,
            result.AbsolutePeakSample,
            result.PositiveSampleCount,
            result.NegativeSampleCount,
            result.ZeroSampleCount,
            result.PositiveFullScaleSampleCount,
            result.NegativeFullScaleSampleCount,
            result.AdjacentOppositeSignCrossingCount,
            result.SampleSum,
            result.SquaredSampleSum,
            MediaQualityState.NotAssessed,
            ModelApplicabilityState.NotAssessed,
            observedAtUtc);

    private static string BuildAudioObservationJobRelativePath(
        Guid jobId,
        DateTimeOffset createdAtUtc)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("The processing job ID cannot be empty.", nameof(jobId));
        }

        var directoryName = string.Create(
            CultureInfo.InvariantCulture,
            $"{createdAtUtc.ToUniversalTime():yyyyMMdd'T'HHmmssfffffff'Z'}_audio-observation_{jobId.ToString("N")[..12]}");
        return Path.Combine("Processing", directoryName);
    }

    private static bool IsExpectedAudioObservationWorkflowException(Exception exception) =>
        exception is ArgumentException
            or ArithmeticException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or KeyNotFoundException
            or DbException
            or FormatException
            or AudioPcmObservationException;

    private static string GetSafeAudioObservationFailureMessage(
        Exception exception,
        bool jobStarted)
    {
        if (exception is AudioPcmObservationException observationException)
        {
            return observationException.Failure switch
            {
                AudioPcmObservationFailure.PreparedOperationalFailure =>
                    "The prepared analysis audio is temporarily unavailable. Its persisted integrity state was not changed; close other software using the workspace files and try again.",
                AudioPcmObservationFailure.WorkspaceInvalid =>
                    "The profile workspace is unavailable or invalid. No objective audio observation result was accepted.",
                _ =>
                    "The prepared analysis audio did not match the frozen PCM observation contract. No result was accepted for that asset.",
            };
        }

        if (exception is ProfileProcessingActiveException)
        {
            return "This profile already has an active processing job, possibly in another app window. Refresh after it finishes.";
        }

        if (exception is ProfileConcurrencyConflictException)
        {
            return "The profile changed before objective audio observation could start. Refresh the profile list and try again.";
        }

        return jobStarted
            ? "Objective audio observation extraction failed safely. No partial result was accepted; the bounded Processing job folder was retained for inspection."
            : "Objective audio observation extraction did not start. Refresh the profile and try again.";
    }
}
