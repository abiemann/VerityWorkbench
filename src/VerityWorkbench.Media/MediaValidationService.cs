using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media;

/// <summary>
/// Validates one immutable workspace MP4 using pinned ffprobe and ffmpeg tools.
/// Success requires both normalized header metadata and a complete software
/// decode of exactly one selected video and audio stream.
/// </summary>
public sealed class MediaValidationService
{
    private const int MaximumToolStandardOutputBytes = 16 * 1024 * 1024;
    private const int MaximumToolStandardErrorBytes = 1024 * 1024;
    private const int MaximumDimension = 32_768;
    private const long MaximumPixelCount = 268_435_456;
    private const double MaximumFramesPerSecond = 1_000;
    private const int MaximumAudioSampleRateHz = 768_000;
    private const int MaximumAudioChannels = 64;
    private const int MaximumTokenLength = 512;
    private const int MaximumConfigurationLength = 4096;

    public const string CurrentValidationContractVersion = "verityworkbench.media-validation.v1";

    private static readonly IReadOnlyList<string> FfprobeIdentityArguments =
        ["-v", "error", "-show_program_version", "-of", "json"];

    private static readonly IReadOnlyList<string> FfmpegIdentityArguments = ["-version"];

    private readonly IBoundedProcessRunner _processRunner;

    public MediaValidationService()
        : this(new BoundedProcessRunner())
    {
    }

    internal MediaValidationService(IBoundedProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<MediaValidationPreflight> PreflightAsync(
        ProfileWorkspaceLayout layout,
        string processingJobDirectoryPath,
        MediaValidationToolContract tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        cancellationToken.ThrowIfCancellationRequested();

        var workingDirectory = ValidateWorkingDirectory(
            layout,
            processingJobDirectoryPath,
            allowProcessingRoot: true);
        var validatedTools = ValidateToolContract(tools);
        await using var ffprobeReadLock = OpenExecutableReadLock(
            validatedTools.Ffprobe.ExecutablePath);
        await using var ffmpegReadLock = OpenExecutableReadLock(
            validatedTools.Ffmpeg.ExecutablePath);
        var ffprobeHash = await HashOpenExecutableAsync(
                ffprobeReadLock,
                cancellationToken)
            .ConfigureAwait(false);
        var ffmpegHash = await HashOpenExecutableAsync(
                ffmpegReadLock,
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(
                ffprobeHash,
                validatedTools.Ffprobe.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                ffmpegHash,
                validatedTools.Ffmpeg.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                MediaValidationFailure.ToolIntegrityMismatch,
                "A configured media-validation executable does not match its pinned SHA-256.");
        }

        var ffprobeProvenance = await ReadFfprobeIdentityAsync(
                validatedTools.Ffprobe,
                workingDirectory,
                ffprobeHash,
                cancellationToken)
            .ConfigureAwait(false);
        var ffmpegProvenance = await ReadFfmpegIdentityAsync(
                validatedTools.Ffmpeg,
                workingDirectory,
                ffmpegHash,
                cancellationToken)
            .ConfigureAwait(false);

        ValidateToolIdentity(
            ffprobeProvenance,
            ffmpegProvenance,
            validatedTools.ExpectedVersionPrefix);

        var contractSha256 = ComputeValidationContractSha256(
            validatedTools,
            ffprobeProvenance,
            ffmpegProvenance);

        return new(ffprobeProvenance, ffmpegProvenance, contractSha256);
    }

    public async Task<ValidatedMediaMetadata> ValidateAsync(
        ProfileWorkspaceLayout layout,
        string processingJobDirectoryPath,
        string mediaFilePath,
        string expectedMediaSha256,
        long expectedMediaByteLength,
        MediaValidationToolContract tools,
        MediaValidationPreflight preflight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(preflight);
        cancellationToken.ThrowIfCancellationRequested();

        var workingDirectory = ValidateWorkingDirectory(
            layout,
            processingJobDirectoryPath,
            allowProcessingRoot: false);
        var boundedMediaPath = ValidateMediaPath(layout, mediaFilePath);
        ValidateExpectedMediaIntegrity(expectedMediaSha256, expectedMediaByteLength);
        var validatedTools = ValidateToolContract(tools);
        ValidatePreflight(validatedTools, preflight);

        await using var mediaReadLock = OpenMediaReadLock(boundedMediaPath);
        await VerifyIntegrityAsync(
                mediaReadLock,
                expectedMediaSha256,
                expectedMediaByteLength,
                cancellationToken)
            .ConfigureAwait(false);

        var probeArguments = new[]
        {
            "-v",
            "error",
            "-protocol_whitelist",
            "file,pipe",
            "-show_format",
            "-show_streams",
            "-of",
            "json",
            boundedMediaPath,
        };
        BoundedProcessResult probeResult;
        await using (var ffprobeReadLock = OpenExecutableReadLock(
                         validatedTools.Ffprobe.ExecutablePath))
        {
            await VerifyExecutableIntegrityAsync(
                    validatedTools.Ffprobe,
                    ffprobeReadLock,
                    cancellationToken)
                .ConfigureAwait(false);
            probeResult = await _processRunner.RunAsync(
                    validatedTools.Ffprobe.ExecutablePath,
                    workingDirectory,
                    probeArguments,
                    validatedTools.Ffprobe.InvocationTimeout,
                    validatedTools.Ffprobe.MaximumStandardOutputBytes,
                    validatedTools.Ffprobe.MaximumStandardErrorBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            EnsureSuccessfulProbe(probeResult);
        }
        catch (MediaValidationException)
        {
            await VerifyCurrentPathIntegrityAsync(
                    layout,
                    boundedMediaPath,
                    mediaReadLock,
                    expectedMediaSha256,
                    expectedMediaByteLength,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        ProbeMetadata probe;
        try
        {
            probe = ParseProbeOutput(probeResult.StandardOutput);
        }
        catch (MediaValidationException)
        {
            await VerifyCurrentPathIntegrityAsync(
                    layout,
                    boundedMediaPath,
                    mediaReadLock,
                    expectedMediaSha256,
                    expectedMediaByteLength,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        var decodeArguments = BuildDecodeArguments(
            boundedMediaPath,
            probe.Video.StreamIndex,
            probe.Audio.StreamIndex);
        BoundedProcessResult decodeResult;
        await using (var ffmpegReadLock = OpenExecutableReadLock(
                         validatedTools.Ffmpeg.ExecutablePath))
        {
            await VerifyExecutableIntegrityAsync(
                    validatedTools.Ffmpeg,
                    ffmpegReadLock,
                    cancellationToken)
                .ConfigureAwait(false);
            decodeResult = await _processRunner.RunAsync(
                    validatedTools.Ffmpeg.ExecutablePath,
                    workingDirectory,
                    decodeArguments,
                    validatedTools.Ffmpeg.InvocationTimeout,
                    validatedTools.Ffmpeg.MaximumStandardOutputBytes,
                    validatedTools.Ffmpeg.MaximumStandardErrorBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Integrity is checked before interpreting the decode result so a
        // concurrent mutation is never misreported as media corruption.
        await VerifyCurrentPathIntegrityAsync(
                layout,
                boundedMediaPath,
                mediaReadLock,
                expectedMediaSha256,
                expectedMediaByteLength,
                cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccessfulDecode(decodeResult);
        var decodedDurationMicroseconds = ParseDecodeProgress(decodeResult.StandardOutput);

        return new(
            probe.ContainerFormat,
            probe.ContainerMajorBrand,
            probe.DurationMicroseconds,
            probe.Video,
            probe.Audio,
            preflight.Ffprobe,
            preflight.Ffmpeg,
            preflight.ValidationContractSha256,
            decodedDurationMicroseconds);
    }

    private async Task<MediaValidationToolProvenance> ReadFfprobeIdentityAsync(
        ValidatedExecutableContract executable,
        string workingDirectory,
        string executableSha256,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
                executable.ExecutablePath,
                workingDirectory,
                FfprobeIdentityArguments,
                executable.PreflightTimeout,
                executable.MaximumStandardOutputBytes,
                executable.MaximumStandardErrorBytes,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessfulIdentity(result);

        try
        {
            using var document = JsonDocument.Parse(
                result.StandardOutput,
                new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("program_version", out var programVersion)
                || programVersion.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            var version = RequireBoundedString(programVersion, "version", MaximumTokenLength);
            var compiler = RequireBoundedString(programVersion, "compiler_ident", MaximumTokenLength);
            var configuration = RequireBoundedString(
                programVersion,
                "configuration",
                MaximumConfigurationLength);
            return BuildToolProvenance(
                version,
                compiler,
                configuration,
                executableSha256);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw Failure(
                MediaValidationFailure.ToolIdentityMalformed,
                "ffprobe returned malformed tool-identity data.");
        }
    }

    private async Task<MediaValidationToolProvenance> ReadFfmpegIdentityAsync(
        ValidatedExecutableContract executable,
        string workingDirectory,
        string executableSha256,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
                executable.ExecutablePath,
                workingDirectory,
                FfmpegIdentityArguments,
                executable.PreflightTimeout,
                executable.MaximumStandardOutputBytes,
                executable.MaximumStandardErrorBytes,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessfulIdentity(result);

        var lines = result.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var versionLine = lines.FirstOrDefault(line => line.StartsWith("ffmpeg version ", StringComparison.Ordinal));
        var compilerLine = lines.FirstOrDefault(line => line.StartsWith("built with ", StringComparison.Ordinal));
        var configurationLine = lines.FirstOrDefault(line => line.StartsWith("configuration: ", StringComparison.Ordinal));

        if (versionLine is null || compilerLine is null || configurationLine is null)
        {
            throw Failure(
                MediaValidationFailure.ToolIdentityMalformed,
                "ffmpeg returned malformed tool-identity data.");
        }

        var versionRemainder = versionLine["ffmpeg version ".Length..];
        var versionSeparator = versionRemainder.IndexOf(' ');
        var version = versionSeparator < 0 ? versionRemainder : versionRemainder[..versionSeparator];
        var compiler = compilerLine["built with ".Length..];
        var configuration = configurationLine["configuration: ".Length..];
        if (!IsBoundedValue(version, MaximumTokenLength)
            || !IsBoundedValue(compiler, MaximumTokenLength)
            || !IsBoundedValue(configuration, MaximumConfigurationLength))
        {
            throw Failure(
                MediaValidationFailure.ToolIdentityMalformed,
                "ffmpeg returned malformed tool-identity data.");
        }

        return BuildToolProvenance(version, compiler, configuration, executableSha256);
    }

    private static void ValidateToolIdentity(
        MediaValidationToolProvenance ffprobe,
        MediaValidationToolProvenance ffmpeg,
        string expectedVersionPrefix)
    {
        if (!MatchesVersionIdentity(ffprobe.Version, expectedVersionPrefix)
            || !MatchesVersionIdentity(ffmpeg.Version, expectedVersionPrefix)
            || !string.Equals(ffprobe.Version, ffmpeg.Version, StringComparison.Ordinal)
            || !string.Equals(
                ffprobe.CompilerIdentifier,
                ffmpeg.CompilerIdentifier,
                StringComparison.Ordinal)
            || !string.Equals(ffprobe.Configuration, ffmpeg.Configuration, StringComparison.Ordinal))
        {
            throw Failure(
                MediaValidationFailure.ToolIdentityMismatch,
                "ffprobe and ffmpeg do not match the pinned build identity.");
        }
    }

    private static bool MatchesVersionIdentity(string version, string expected) =>
        string.Equals(version, expected, StringComparison.Ordinal)
        || (version.StartsWith(expected, StringComparison.Ordinal)
            && version.Length > expected.Length
            && !char.IsLetterOrDigit(version[expected.Length]));

    private static ProbeMetadata ParseProbeOutput(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
            {
                throw Failure(
                    MediaValidationFailure.ProbeOutputMalformed,
                    "ffprobe did not return a format object.");
            }

            var formatName = RequireBoundedString(format, "format_name", MaximumTokenLength);
            if (!HasFormatToken(formatName, "mov") || !HasFormatToken(formatName, "mp4"))
            {
                throw Failure(
                    MediaValidationFailure.UnsupportedContainer,
                    "The selected media is not an MP4 container.");
            }

            var majorBrand = ReadMajorBrand(format);
            if (IsKnownNonMp4MajorBrand(majorBrand))
            {
                throw Failure(
                    MediaValidationFailure.UnsupportedContainer,
                    "QuickTime, 3GP, and 3G2 containers are not accepted as MP4 training media.");
            }

            var durationMicroseconds = ParsePositiveDurationMicroseconds(format);
            if (!root.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array)
            {
                throw Failure(
                    MediaValidationFailure.ProbeOutputMalformed,
                    "ffprobe did not return a stream array.");
            }

            var videos = new List<StreamCandidate<ValidatedVideoStreamMetadata>>();
            var audios = new List<StreamCandidate<ValidatedAudioStreamMetadata>>();
            var realVideoStreamCount = 0;
            var audioStreamCount = 0;
            var seenIndices = new HashSet<int>();

            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.ValueKind != JsonValueKind.Object
                    || !TryReadInt32(stream, "index", out var streamIndex)
                    || streamIndex < 0
                    || !seenIndices.Add(streamIndex))
                {
                    throw Failure(
                        MediaValidationFailure.ProbeOutputMalformed,
                        "ffprobe returned an invalid or duplicate stream index.");
                }

                if (!TryReadString(stream, "codec_type", out var codecType))
                {
                    continue;
                }

                var isDefault = IsDefaultStream(stream);
                if (string.Equals(codecType, "video", StringComparison.Ordinal))
                {
                    if (IsAttachedPicture(stream))
                    {
                        continue;
                    }

                    realVideoStreamCount++;
                    if (TryParseVideoStream(stream, streamIndex, out var video))
                    {
                        videos.Add(new(video, isDefault));
                    }
                }
                else if (string.Equals(codecType, "audio", StringComparison.Ordinal))
                {
                    audioStreamCount++;
                    if (TryParseAudioStream(stream, streamIndex, out var audio))
                    {
                        audios.Add(new(audio, isDefault));
                    }
                }
            }

            var selectedVideo = SelectVideo(videos, realVideoStreamCount);
            var selectedAudio = SelectAudio(audios, audioStreamCount);
            return new(
                "mp4",
                majorBrand,
                durationMicroseconds,
                selectedVideo,
                selectedAudio);
        }
        catch (MediaValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or FormatException
                or OverflowException
                or ArgumentException)
        {
            throw Failure(
                MediaValidationFailure.ProbeOutputMalformed,
                "ffprobe returned malformed media metadata.");
        }
    }

    private static ValidatedVideoStreamMetadata SelectVideo(
        IReadOnlyList<StreamCandidate<ValidatedVideoStreamMetadata>> candidates,
        int realVideoStreamCount)
    {
        if (realVideoStreamCount == 0)
        {
            throw Failure(
                MediaValidationFailure.MissingVideoStream,
                "The MP4 does not contain a real video stream.");
        }

        if (candidates.Count != realVideoStreamCount)
        {
            throw Failure(
                MediaValidationFailure.InvalidVideoStream,
                "Every real MP4 video stream must have sane decodable metadata.");
        }

        return SelectUnambiguous(
            candidates,
            MediaValidationFailure.AmbiguousVideoStreams,
            "The MP4 has multiple usable video streams without exactly one default.");
    }

    private static ValidatedAudioStreamMetadata SelectAudio(
        IReadOnlyList<StreamCandidate<ValidatedAudioStreamMetadata>> candidates,
        int audioStreamCount)
    {
        if (audioStreamCount == 0)
        {
            throw Failure(
                MediaValidationFailure.MissingAudioStream,
                "The MP4 does not contain an audio stream required for multimodal analysis.");
        }

        if (candidates.Count != audioStreamCount)
        {
            throw Failure(
                MediaValidationFailure.InvalidAudioStream,
                "Every MP4 audio stream must have sane decodable metadata.");
        }

        return SelectUnambiguous(
            candidates,
            MediaValidationFailure.AmbiguousAudioStreams,
            "The MP4 has multiple usable audio streams without exactly one default.");
    }

    private static T SelectUnambiguous<T>(
        IReadOnlyList<StreamCandidate<T>> candidates,
        MediaValidationFailure failure,
        string failureMessage)
    {
        if (candidates.Count == 1)
        {
            return candidates[0].Metadata;
        }

        var defaults = candidates.Where(candidate => candidate.IsDefault).ToArray();
        if (defaults.Length == 1)
        {
            return defaults[0].Metadata;
        }

        throw Failure(failure, failureMessage);
    }

    private static bool TryParseVideoStream(
        JsonElement stream,
        int streamIndex,
        out ValidatedVideoStreamMetadata metadata)
    {
        metadata = null!;
        if (!TryReadBoundedString(stream, "codec_name", MaximumTokenLength, out var codec)
            || !TryReadInt32(stream, "width", out var width)
            || !TryReadInt32(stream, "height", out var height)
            || width <= 0
            || height <= 0
            || width > MaximumDimension
            || height > MaximumDimension
            || (long)width * height > MaximumPixelCount
            || !TryReadFrameRate(stream, out var numerator, out var denominator))
        {
            return false;
        }

        var framesPerSecond = (double)numerator / denominator;
        if (!double.IsFinite(framesPerSecond)
            || framesPerSecond <= 0
            || framesPerSecond > MaximumFramesPerSecond)
        {
            return false;
        }

        metadata = new(streamIndex, codec, width, height, numerator, denominator);
        return true;
    }

    private static bool TryParseAudioStream(
        JsonElement stream,
        int streamIndex,
        out ValidatedAudioStreamMetadata metadata)
    {
        metadata = null!;
        if (!TryReadBoundedString(stream, "codec_name", MaximumTokenLength, out var codec)
            || !TryReadInt32Flexible(stream, "sample_rate", out var sampleRate)
            || !TryReadInt32(stream, "channels", out var channels)
            || sampleRate <= 0
            || sampleRate > MaximumAudioSampleRateHz
            || channels <= 0
            || channels > MaximumAudioChannels)
        {
            return false;
        }

        metadata = new(streamIndex, codec, sampleRate, channels);
        return true;
    }

    private static bool TryReadFrameRate(
        JsonElement stream,
        out long numerator,
        out long denominator)
    {
        numerator = 0;
        denominator = 0;
        if (TryReadString(stream, "avg_frame_rate", out var average)
            && TryParsePositiveRational(average, out numerator, out denominator))
        {
            return true;
        }

        return TryReadString(stream, "r_frame_rate", out var raw)
            && TryParsePositiveRational(raw, out numerator, out denominator);
    }

    private static bool TryParsePositiveRational(
        string value,
        out long numerator,
        out long denominator)
    {
        numerator = 0;
        denominator = 0;
        var separator = value.IndexOf('/');
        return separator > 0
            && separator == value.LastIndexOf('/')
            && long.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out numerator)
            && long.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out denominator)
            && numerator > 0
            && denominator > 0;
    }

    private static long ParsePositiveDurationMicroseconds(JsonElement format)
    {
        if (!format.TryGetProperty("duration", out var durationElement))
        {
            throw Failure(
                MediaValidationFailure.InvalidDuration,
                "The MP4 duration is missing or invalid.");
        }

        var value = durationElement.ValueKind switch
        {
            JsonValueKind.String => durationElement.GetString(),
            JsonValueKind.Number => durationElement.GetRawText(),
            _ => null,
        };
        if (value is null
            || !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0
            || seconds > long.MaxValue / 1_000_000m)
        {
            throw Failure(
                MediaValidationFailure.InvalidDuration,
                "The MP4 duration is missing or invalid.");
        }

        var microseconds = decimal.ToInt64(decimal.Round(
            seconds * 1_000_000m,
            0,
            MidpointRounding.AwayFromZero));
        if (microseconds <= 0)
        {
            throw Failure(
                MediaValidationFailure.InvalidDuration,
                "The MP4 duration is missing or invalid.");
        }

        return microseconds;
    }

    private static string? ReadMajorBrand(JsonElement format)
    {
        if (!format.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!tags.TryGetProperty("major_brand", out var majorBrandElement)
            || majorBrandElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var majorBrand = majorBrandElement.GetString();
        if (string.IsNullOrWhiteSpace(majorBrand))
        {
            return null;
        }

        if (majorBrand.Length != 4 || majorBrand.Any(character => character is < ' ' or > '~'))
        {
            return null;
        }

        return majorBrand;
    }

    private static bool IsKnownNonMp4MajorBrand(string? majorBrand)
    {
        if (majorBrand is null)
        {
            return false;
        }

        var normalized = majorBrand.TrimEnd();
        return string.Equals(normalized, "qt", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("3gp", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("3g2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefaultStream(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition)
        && disposition.ValueKind == JsonValueKind.Object
        && TryReadInt32(disposition, "default", out var value)
        && value == 1;

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition)
        && disposition.ValueKind == JsonValueKind.Object
        && TryReadInt32(disposition, "attached_pic", out var value)
        && value == 1;

    private static IReadOnlyList<string> BuildDecodeArguments(
        string mediaFilePath,
        int videoStreamIndex,
        int audioStreamIndex) =>
        [
            "-nostdin",
            "-v",
            "error",
            "-xerror",
            "-hwaccel",
            "none",
            "-protocol_whitelist",
            "file,pipe",
            "-stats_period",
            "10",
            "-progress",
            "pipe:1",
            "-nostats",
            "-i",
            mediaFilePath,
            "-map",
            $"0:{videoStreamIndex.ToString(CultureInfo.InvariantCulture)}",
            "-map",
            $"0:{audioStreamIndex.ToString(CultureInfo.InvariantCulture)}",
            "-map_metadata",
            "-1",
            "-map_chapters",
            "-1",
            "-sn",
            "-dn",
            "-f",
            "null",
            "-",
        ];

    private static long ParseDecodeProgress(string output)
    {
        long decodedDuration = 0;
        string? finalProgress = null;
        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var separator = rawLine.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = rawLine[..separator];
            var value = rawLine[(separator + 1)..].Trim();
            if (string.Equals(name, "out_time_us", StringComparison.Ordinal)
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                decodedDuration = parsed;
            }
            else if (string.Equals(name, "progress", StringComparison.Ordinal))
            {
                finalProgress = value;
            }
        }

        if (!string.Equals(finalProgress, "end", StringComparison.Ordinal)
            || decodedDuration <= 0)
        {
            throw Failure(
                MediaValidationFailure.DecodeProgressMalformed,
                "ffmpeg did not report a complete decode progress record.");
        }

        return decodedDuration;
    }

    private static void EnsureSuccessfulIdentity(BoundedProcessResult result)
    {
        switch (result.Termination)
        {
            case ProcessTermination.LaunchFailed:
                throw Failure(MediaValidationFailure.ToolLaunchFailed, "A media-validation tool could not start.");
            case ProcessTermination.TimedOut:
                throw Failure(MediaValidationFailure.ToolIdentityTimedOut, "A media-validation tool identity check timed out.");
            case ProcessTermination.StandardOutputLimitExceeded:
            case ProcessTermination.StandardErrorLimitExceeded:
                throw Failure(
                    MediaValidationFailure.ToolIdentityOutputLimitExceeded,
                    "A media-validation tool identity check exceeded its output limit.");
            case ProcessTermination.Exited when result.ExitCode != 0:
                throw Failure(
                    MediaValidationFailure.ToolIdentityMalformed,
                    "A media-validation tool identity check failed.");
        }
    }

    private static void EnsureSuccessfulProbe(BoundedProcessResult result)
    {
        switch (result.Termination)
        {
            case ProcessTermination.LaunchFailed:
                throw Failure(MediaValidationFailure.ProbeLaunchFailed, "ffprobe could not start.");
            case ProcessTermination.TimedOut:
                throw Failure(MediaValidationFailure.ProbeTimedOut, "MP4 probing timed out.");
            case ProcessTermination.StandardOutputLimitExceeded:
            case ProcessTermination.StandardErrorLimitExceeded:
                throw Failure(
                    MediaValidationFailure.ProbeOutputLimitExceeded,
                    "MP4 probing exceeded its output limit.");
            case ProcessTermination.Exited when result.ExitCode != 0:
                throw Failure(
                    MediaValidationFailure.ProbeRejectedMedia,
                    "ffprobe could not read the selected MP4.");
        }
    }

    private static void EnsureSuccessfulDecode(BoundedProcessResult result)
    {
        switch (result.Termination)
        {
            case ProcessTermination.LaunchFailed:
                throw Failure(MediaValidationFailure.DecodeLaunchFailed, "ffmpeg could not start.");
            case ProcessTermination.TimedOut:
                throw Failure(MediaValidationFailure.DecodeTimedOut, "Full MP4 decoding timed out.");
            case ProcessTermination.StandardOutputLimitExceeded:
            case ProcessTermination.StandardErrorLimitExceeded:
                throw Failure(
                    MediaValidationFailure.DecodeOutputLimitExceeded,
                    "Full MP4 decoding exceeded its output limit.");
            case ProcessTermination.Exited when result.ExitCode != 0:
                if (IndicatesUnsupportedCodec(result.StandardError))
                {
                    throw Failure(
                        MediaValidationFailure.UnsupportedCodec,
                        "A selected media stream uses a codec unavailable in the pinned FFmpeg build.");
                }

                throw Failure(
                    MediaValidationFailure.CorruptMedia,
                    "The selected streams could not be decoded completely.");
        }
    }

    private static bool IndicatesUnsupportedCodec(string standardError)
    {
        string[] indicators =
        [
            "decoder not found",
            "no decoder found",
            "unknown decoder",
            "unsupported codec",
            "decoding requested, but no decoder",
        ];
        return indicators.Any(indicator =>
            standardError.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidatePreflight(
        ValidatedToolContract tools,
        MediaValidationPreflight preflight)
    {
        if (!IsSha256(preflight.ValidationContractSha256)
            || !IsValidProvenance(preflight.Ffprobe)
            || !IsValidProvenance(preflight.Ffmpeg)
            || !string.Equals(
                tools.Ffprobe.ExpectedSha256,
                preflight.Ffprobe.ExecutableSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                tools.Ffmpeg.ExpectedSha256,
                preflight.Ffmpeg.ExecutableSha256,
                StringComparison.Ordinal))
        {
            throw Failure(
                MediaValidationFailure.ToolIdentityMismatch,
                "The media-validation preflight does not match the supplied tool contract.");
        }

        ValidateToolIdentity(preflight.Ffprobe, preflight.Ffmpeg, tools.ExpectedVersionPrefix);
        var expectedContractSha256 = ComputeValidationContractSha256(
            tools,
            preflight.Ffprobe,
            preflight.Ffmpeg);
        if (!string.Equals(
                expectedContractSha256,
                preflight.ValidationContractSha256,
                StringComparison.Ordinal))
        {
            throw Failure(
                MediaValidationFailure.ToolIdentityMismatch,
                "The media-validation preflight does not match the supplied tool contract.");
        }
    }

    private static bool IsValidProvenance(MediaValidationToolProvenance provenance) =>
        provenance is not null
        && IsBoundedValue(provenance.Version, MaximumTokenLength)
        && IsBoundedValue(provenance.CompilerIdentifier, MaximumTokenLength)
        && IsBoundedValue(provenance.Configuration, MaximumConfigurationLength)
        && IsSha256(provenance.ConfigurationSha256)
        && IsSha256(provenance.ExecutableSha256)
        && string.Equals(
            provenance.ConfigurationSha256,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provenance.Configuration))),
            StringComparison.Ordinal);

    private static ValidatedToolContract ValidateToolContract(MediaValidationToolContract tools)
    {
        if (tools is null || tools.Ffprobe is null || tools.Ffmpeg is null)
        {
            throw Failure(
                MediaValidationFailure.ToolContractInvalid,
                "Both ffprobe and ffmpeg contracts are required.");
        }

        if (!IsBoundedValue(tools.ExpectedVersionPrefix, 128)
            || !string.Equals(
                tools.ValidationContractVersion,
                CurrentValidationContractVersion,
                StringComparison.Ordinal))
        {
            throw Failure(
                MediaValidationFailure.ToolContractInvalid,
                "The expected media-tool version prefix is invalid.");
        }

        return new(
            ValidateExecutableContract(tools.Ffprobe),
            ValidateExecutableContract(tools.Ffmpeg),
            tools.ExpectedVersionPrefix,
            tools.ValidationContractVersion);
    }

    private static ValidatedExecutableContract ValidateExecutableContract(
        MediaValidationExecutableContract executable)
    {
        string path;
        try
        {
            if (string.IsNullOrWhiteSpace(executable.ExecutablePath)
                || !Path.IsPathFullyQualified(executable.ExecutablePath))
            {
                throw new ArgumentException();
            }

            path = Path.GetFullPath(executable.ExecutablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure(
                MediaValidationFailure.ToolContractInvalid,
                "A media-validation executable path is invalid.");
        }

        if (!File.Exists(path))
        {
            throw Failure(
                MediaValidationFailure.ToolUnavailable,
                "A configured media-validation executable is unavailable.");
        }

        if (!IsSha256(executable.ExpectedSha256)
            || executable.PreflightTimeout <= TimeSpan.Zero
            || executable.PreflightTimeout > TimeSpan.FromDays(1)
            || executable.InvocationTimeout <= TimeSpan.Zero
            || executable.InvocationTimeout > TimeSpan.FromDays(1)
            || executable.MaximumStandardOutputBytes <= 0
            || executable.MaximumStandardOutputBytes > MaximumToolStandardOutputBytes
            || executable.MaximumStandardErrorBytes <= 0
            || executable.MaximumStandardErrorBytes > MaximumToolStandardErrorBytes)
        {
            throw Failure(
                MediaValidationFailure.ToolContractInvalid,
                "A media-validation executable contract is invalid.");
        }

        return new(
            path,
            executable.ExpectedSha256.ToLowerInvariant(),
            executable.PreflightTimeout,
            executable.InvocationTimeout,
            executable.MaximumStandardOutputBytes,
            executable.MaximumStandardErrorBytes);
    }

    private static string ValidateMediaPath(ProfileWorkspaceLayout layout, string mediaFilePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mediaFilePath)
                || !Path.IsPathFullyQualified(mediaFilePath))
            {
                throw new ArgumentException();
            }

            var mediaRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(layout.MediaRoot));
            var candidate = Path.GetFullPath(mediaFilePath);
            var relative = Path.GetRelativePath(mediaRoot, candidate);
            if (Path.IsPathFullyQualified(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Equals(".", StringComparison.Ordinal)
                || !Path.GetExtension(candidate).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate))
            {
                throw new IOException();
            }

            EnsureNoReparsePoints(mediaRoot, candidate);
            return candidate;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            throw Failure(
                MediaValidationFailure.MediaPathInvalid,
                "The media path must be an existing MP4 contained beneath the workspace Media folder.");
        }
    }

    private static string ValidateWorkingDirectory(
        ProfileWorkspaceLayout layout,
        string processingJobDirectoryPath,
        bool allowProcessingRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(processingJobDirectoryPath)
                || !Path.IsPathFullyQualified(processingJobDirectoryPath))
            {
                throw new ArgumentException();
            }

            var processingRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(layout.ProcessingRoot));
            var candidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(processingJobDirectoryPath));
            var parent = Directory.GetParent(candidate);
            var isProcessingRoot = PathsEqual(processingRoot, candidate);
            if ((!allowProcessingRoot || !isProcessingRoot)
                && (parent is null || !PathsEqual(processingRoot, parent.FullName)))
            {
                throw new IOException();
            }

            if (!Directory.Exists(candidate))
            {
                throw new IOException();
            }

            EnsureNoReparsePoints(processingRoot, candidate);
            return candidate;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {
            throw Failure(
                MediaValidationFailure.WorkingDirectoryInvalid,
                allowProcessingRoot
                    ? "The preflight working directory must be the workspace Processing folder or one of its existing direct children."
                    : "The validation working directory must be an existing direct child of the workspace Processing folder.");
        }
    }

    private static void ValidateExpectedMediaIntegrity(string expectedSha256, long expectedByteLength)
    {
        if (!IsSha256(expectedSha256) || expectedByteLength <= 0)
        {
            throw Failure(
                MediaValidationFailure.MediaIntegrityMetadataInvalid,
                "Expected media integrity metadata is invalid.");
        }
    }

    private static FileStream OpenMediaReadLock(string mediaFilePath)
    {
        try
        {
            return new FileStream(
                mediaFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Failure(
                MediaValidationFailure.IntegrityChanged,
                "The workspace media could not be locked for validation.");
        }
    }

    private static async Task VerifyCurrentPathIntegrityAsync(
        ProfileWorkspaceLayout layout,
        string mediaFilePath,
        FileStream lockedStream,
        string expectedSha256,
        long expectedByteLength,
        CancellationToken cancellationToken)
    {
        await VerifyIntegrityAsync(
                lockedStream,
                expectedSha256,
                expectedByteLength,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            _ = ValidateMediaPath(layout, mediaFilePath);
        }
        catch (MediaValidationException exception)
            when (exception.Failure == MediaValidationFailure.MediaPathInvalid)
        {
            throw Failure(
                MediaValidationFailure.IntegrityChanged,
                "The workspace media path changed during validation.");
        }
        try
        {
            await using var currentPathStream = new FileStream(
                mediaFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await VerifyIntegrityAsync(
                    currentPathStream,
                    expectedSha256,
                    expectedByteLength,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MediaValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw Failure(
                MediaValidationFailure.IntegrityChanged,
                "The workspace media changed during validation.");
        }
    }

    private static async Task VerifyIntegrityAsync(
        FileStream stream,
        string expectedSha256,
        long expectedByteLength,
        CancellationToken cancellationToken)
    {
        try
        {
            if (stream.Length != expectedByteLength)
            {
                throw Failure(
                    MediaValidationFailure.IntegrityChanged,
                    "The workspace media byte length changed during validation.");
            }

            stream.Position = 0;
            var hash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Failure(
                    MediaValidationFailure.IntegrityChanged,
                    "The workspace media SHA-256 changed during validation.");
            }
        }
        catch (MediaValidationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(
                MediaValidationFailure.IntegrityChanged,
                "The workspace media changed during validation.");
        }
    }

    private static FileStream OpenExecutableReadLock(string executablePath)
    {
        try
        {
            if ((File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException();
            }

            return new FileStream(
                executablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(
                MediaValidationFailure.ToolUnavailable,
                "A configured media-validation executable could not be locked for use.");
        }
    }

    private static async Task<string> HashOpenExecutableAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            stream.Position = 0;
            return Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure(
                MediaValidationFailure.ToolUnavailable,
                "A configured media-validation executable could not be read.");
        }
    }

    private static async Task VerifyExecutableIntegrityAsync(
        ValidatedExecutableContract executable,
        FileStream executableReadLock,
        CancellationToken cancellationToken)
    {
        var currentHash = await HashOpenExecutableAsync(
                executableReadLock,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                currentHash,
                executable.ExpectedSha256,
                StringComparison.Ordinal))
        {
            throw Failure(
                MediaValidationFailure.ToolIntegrityMismatch,
                "A media-validation executable changed after preflight.");
        }
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        EnsureNotReparsePoint(root);
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException();
        }
    }

    private static MediaValidationToolProvenance BuildToolProvenance(
        string version,
        string compiler,
        string configuration,
        string executableSha256) =>
        new(
            version,
            compiler,
            configuration,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configuration))),
            executableSha256);

    private static string ComputeValidationContractSha256(
        ValidatedToolContract tools,
        MediaValidationToolProvenance ffprobe,
        MediaValidationToolProvenance ffmpeg)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, "contract", tools.ValidationContractVersion);
        AppendCanonical(builder, "expectedVersionPrefix", tools.ExpectedVersionPrefix);
        AppendCanonical(builder, "ffprobe.sha256", ffprobe.ExecutableSha256);
        AppendCanonical(builder, "ffprobe.version", ffprobe.Version);
        AppendCanonical(builder, "ffprobe.compiler", ffprobe.CompilerIdentifier);
        AppendCanonical(builder, "ffprobe.configurationSha256", ffprobe.ConfigurationSha256);
        AppendCanonical(builder, "ffprobe.preflightTimeoutTicks", tools.Ffprobe.PreflightTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "ffprobe.invocationTimeoutTicks", tools.Ffprobe.InvocationTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "ffprobe.stdoutLimit", tools.Ffprobe.MaximumStandardOutputBytes.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "ffprobe.stderrLimit", tools.Ffprobe.MaximumStandardErrorBytes.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "ffmpeg.sha256", ffmpeg.ExecutableSha256);
        AppendCanonical(builder, "ffmpeg.version", ffmpeg.Version);
        AppendCanonical(builder, "ffmpeg.compiler", ffmpeg.CompilerIdentifier);
        AppendCanonical(builder, "ffmpeg.configurationSha256", ffmpeg.ConfigurationSha256);
        AppendCanonical(builder, "ffmpeg.preflightTimeoutTicks", tools.Ffmpeg.PreflightTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "ffmpeg.invocationTimeoutTicks", tools.Ffmpeg.InvocationTimeout.Ticks.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "ffmpeg.stdoutLimit", tools.Ffmpeg.MaximumStandardOutputBytes.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "ffmpeg.stderrLimit", tools.Ffmpeg.MaximumStandardErrorBytes.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "container", "mp4-extension;mov-and-mp4-demuxer;reject-qt-3gp-3g2-major-brand");
        AppendCanonical(builder, "selection", "all-real-streams-sane;one-video-and-one-audio;exactly-one-default-if-multiple-usable");
        AppendCanonical(builder, "protocolWhitelist", "file,pipe");
        AppendCanonical(builder, "decode", "software;xerror;selected-streams;null-output");
        AppendCanonical(builder, "maxDimension", MaximumDimension.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "maxPixelCount", MaximumPixelCount.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "maxFps", MaximumFramesPerSecond.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "maxSampleRate", MaximumAudioSampleRateHz.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "maxChannels", MaximumAudioChannels.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendCanonical(StringBuilder builder, string name, string value) =>
        builder.Append(name)
            .Append('=')
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static string RequireBoundedString(JsonElement parent, string name, int maximumLength)
    {
        if (!TryReadBoundedString(parent, name, maximumLength, out var value))
        {
            throw new JsonException();
        }

        return value;
    }

    private static bool TryReadBoundedString(
        JsonElement parent,
        string name,
        int maximumLength,
        out string value) =>
        TryReadString(parent, name, out value) && IsBoundedValue(value, maximumLength);

    private static bool IsBoundedValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);

    private static bool TryReadInt32(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private static bool TryReadInt32Flexible(JsonElement parent, string name, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                element.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out value),
            _ => false,
        };
    }

    private static bool HasFormatToken(string formatName, string expected) =>
        formatName.Split(',', StringSplitOptions.TrimEntries)
            .Contains(expected, StringComparer.OrdinalIgnoreCase);

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static MediaValidationException Failure(MediaValidationFailure failure, string message) =>
        new(failure, message);

    private sealed record StreamCandidate<T>(T Metadata, bool IsDefault);

    private sealed record ProbeMetadata(
        string ContainerFormat,
        string? ContainerMajorBrand,
        long DurationMicroseconds,
        ValidatedVideoStreamMetadata Video,
        ValidatedAudioStreamMetadata Audio);

    private sealed record ValidatedExecutableContract(
        string ExecutablePath,
        string ExpectedSha256,
        TimeSpan PreflightTimeout,
        TimeSpan InvocationTimeout,
        int MaximumStandardOutputBytes,
        int MaximumStandardErrorBytes);

    private sealed record ValidatedToolContract(
        ValidatedExecutableContract Ffprobe,
        ValidatedExecutableContract Ffmpeg,
        string ExpectedVersionPrefix,
        string ValidationContractVersion);
}
