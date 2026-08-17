using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media;

public sealed partial class MediaPreprocessingService
{
    /// <summary>
    /// Verifies the complete committed prepared-media bundle and returns the exact
    /// analysis-audio handle whose bytes were checked. The open handle denies write
    /// and delete sharing, so later analysis cannot be redirected to replacement bytes.
    /// </summary>
    public async Task<PreparedMediaAnalysisAudioOpenResult> OpenVerifiedAnalysisAudioAsync(
        ProfileWorkspaceLayout layout,
        MediaPreprocessingResult committed,
        CancellationToken cancellationToken = default)
    {
        FileStream? audioStream = null;
        try
        {
            ValidateLayout(layout);
            ArgumentNullException.ThrowIfNull(committed);
            var preparedDirectory = ValidatePreparedDirectoryPath(
                layout,
                committed,
                requireExists: true);
            var audioPath = ResolveRelativePath(
                layout,
                committed.AnalysisAudioWorkspaceRelativePath,
                "The prepared analysis-audio path escapes Media.");
            var audioAttributes = File.GetAttributes(audioPath);
            if ((audioAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "The prepared analysis audio must be a regular file inside the prepared-media bundle.");
            }

            audioStream = new FileStream(
                audioPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await VerifyOpenAnalysisAudioStreamAsync(
                    audioStream,
                    committed.AnalysisAudioSha256,
                    committed.AnalysisAudioByteLength,
                    cancellationToken)
                .ConfigureAwait(false);

            // Reverify every prepared artifact while the exact WAV is locked
            // against write/delete replacement.
            await VerifyBundleDirectoryAsync(preparedDirectory, committed, cancellationToken)
                .ConfigureAwait(false);
            audioStream.Position = 0;
            var lease = new PreparedMediaAnalysisAudioLease(audioStream);
            audioStream = null;
            return new(MediaPreparedVerificationState.Verified, lease, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is MediaIntegrityException
                or ArgumentException
                or InvalidDataException
                or FileNotFoundException
                or DirectoryNotFoundException
                or InvalidOperationException)
        {
            return new(
                MediaPreparedVerificationState.IntegrityMismatch,
                Lease: null,
                "The committed prepared-media bundle is missing, unsafe, or no longer matches its recorded integrity metadata.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new(
                MediaPreparedVerificationState.OperationalFailure,
                Lease: null,
                "The committed prepared-media bundle could not be read. Its integrity state was not changed.");
        }
        finally
        {
            if (audioStream is not null)
            {
                await audioStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task VerifyOpenAnalysisAudioStreamAsync(
        FileStream stream,
        string expectedSha256,
        long expectedByteLength,
        CancellationToken cancellationToken)
    {
        if (!IsLowercaseSha256(expectedSha256) || expectedByteLength <= 0)
        {
            throw new InvalidDataException(
                "The prepared analysis audio has invalid integrity metadata.");
        }

        if (!stream.CanRead || !stream.CanSeek || stream.Length != expectedByteLength)
        {
            throw new InvalidDataException(
                "The prepared analysis audio is missing or changed length.");
        }

        stream.Position = 0;
        var currentSha256 = Convert.ToHexStringLower(
            await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false));
        if (!string.Equals(currentSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The prepared analysis audio changed hash.");
        }

        stream.Position = 0;
    }
}
