using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media;

/// <summary>
/// Creates a bounded, reproducible playback proxy and analysis WAV from one
/// already validated immutable workspace MP4. All generation occurs below the
/// caller's processing job. Promotion is a separate atomic directory move.
/// </summary>
public sealed partial class MediaPreprocessingService
{
    public const string CurrentPreprocessingContractVersion =
        "verityworkbench.media-preprocessing.v1";

    public const string CurrentTimestampMapVersion =
        "verityworkbench.timestamp-map.v1";

    public const string NotAssessed = "NotAssessed";

    private const int CopyBufferSize = 128 * 1024;
    private const int MaximumJournalBytes = 128 * 1024;
    private const int JournalVersion = 1;
    private const int ContractHashPrefixLength = 12;
    private const int ProxyFrameRate = 30;
    private const int ProxyMaximumWidth = 1280;
    private const int ProxyMaximumHeight = 720;
    private const int ProxyAudioSampleRate = 48_000;
    private const int ProxyAudioChannels = 2;
    private const int AnalysisAudioSampleRate = 16_000;
    private const int AnalysisAudioChannels = 1;

    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly string[] TimestampLimitations =
    [
        "This is an affine target-time map, not exact source-frame lineage.",
        "The 30 fps proxy may select, duplicate, or omit source frames.",
        "Asynchronous audio resampling may pad, trim, or compensate timestamp discontinuities.",
        "Microsecond and sample conversions are rounded to the nearest representable unit.",
    ];

    private readonly IBoundedProcessRunner _processRunner;

    public MediaPreprocessingService()
        : this(new BoundedProcessRunner())
    {
    }

    internal MediaPreprocessingService(IBoundedProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<StagedMediaPreprocessingResult> PrepareAsync(
        ProfileWorkspaceLayout layout,
        string processingJobDirectoryPath,
        MediaPreprocessingRequest request,
        MediaValidationToolContract tools,
        MediaValidationPreflight preflight,
        IProgress<MediaPreprocessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preflight);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateRequest(request);
        ValidateLayout(layout);
        var jobDirectory = ValidateJobDirectory(layout, processingJobDirectoryPath);
        var sourcePath = ValidateSourcePath(layout, request.MediaFilePath, request.MediaAssetId);
        var validatedTools = ValidateTools(tools, preflight);
        if (!string.Equals(
                request.Validation.ValidationContractSha256,
                preflight.ValidationContractSha256,
                StringComparison.Ordinal))
        {
            throw Failure(
                MediaPreprocessingFailure.PreflightMismatch,
                "The stored media-validation result does not match the active validation contract.");
        }

        var contractSha256 = ComputePreprocessingContractSha256(preflight);
        var intendedPreparedDirectory = BuildPreparedDirectory(
            layout,
            sourcePath,
            contractSha256);

        if (Directory.Exists(intendedPreparedDirectory) || File.Exists(intendedPreparedDirectory))
        {
            throw Failure(
                MediaPreprocessingFailure.ArtifactIntegrityInvalid,
                "The immutable prepared-media contract already exists for this asset.");
        }

        var outputRoot = RequireContainedPath(
            jobDirectory,
            Path.Combine(jobDirectory, "Output"),
            "The preprocessing output path escapes its job.");
        Directory.CreateDirectory(outputRoot);
        EnsurePathSegmentsHaveNoReparsePoints(jobDirectory, outputRoot);

        var stagedDirectory = RequireContainedPath(
            outputRoot,
            Path.Combine(outputRoot, request.MediaAssetId.ToString("N")),
            "The staged preprocessing path escapes its output folder.");
        RequireDirectChild(
            outputRoot,
            stagedDirectory,
            "A staged preprocessing item must be directly beneath Output.");
        if (Directory.Exists(stagedDirectory) || File.Exists(stagedDirectory))
        {
            throw Failure(
                MediaPreprocessingFailure.ProcessingPathInvalid,
                "The staged preprocessing item already exists.");
        }

        Directory.CreateDirectory(stagedDirectory);
        EnsureNotReparsePoint(stagedDirectory);

        var proxyPartPath = Path.Combine(stagedDirectory, "proxy.mp4.part");
        var audioPartPath = Path.Combine(stagedDirectory, "audio.wav.part");
        var proxyPath = Path.Combine(stagedDirectory, "proxy.mp4");
        var audioPath = Path.Combine(stagedDirectory, "audio.wav");
        var timestampMapPath = Path.Combine(stagedDirectory, "timestamp-map.json");
        var manifestPath = Path.Combine(stagedDirectory, "preprocessing-manifest.json");

        await using var sourceReadLock = OpenSourceReadLock(sourcePath);
        await VerifyIntegrityAsync(
                sourceReadLock,
                request.ExpectedSourceSha256,
                request.ExpectedSourceByteLength,
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new(request.MediaAssetId, MediaPreprocessingPhase.ProbingTimeline));
        var firstVideoPts = await ProbeFirstDecodedPtsAsync(
                validatedTools.Ffprobe,
                jobDirectory,
                sourcePath,
                request.Validation.Video.StreamIndex,
                cancellationToken)
            .ConfigureAwait(false);
        var firstAudioPts = await ProbeFirstDecodedPtsAsync(
                validatedTools.Ffprobe,
                jobDirectory,
                sourcePath,
                request.Validation.Audio.StreamIndex,
                cancellationToken)
            .ConfigureAwait(false);
        var sourceTimelineOrigin = Math.Min(firstVideoPts, firstAudioPts);

        progress?.Report(new(request.MediaAssetId, MediaPreprocessingPhase.GeneratingArtifacts));
        var generationArguments = BuildGenerationArguments(
            sourcePath,
            request.Validation.Video.StreamIndex,
            request.Validation.Audio.StreamIndex,
            sourceTimelineOrigin,
            proxyPartPath,
            audioPartPath);
        BoundedProcessResult generation;
        await using (var ffmpegReadLock = OpenExecutableReadLock(validatedTools.Ffmpeg.ExecutablePath))
        {
            await VerifyExecutableIntegrityAsync(
                    validatedTools.Ffmpeg,
                    ffmpegReadLock,
                    cancellationToken)
                .ConfigureAwait(false);
            generation = await _processRunner.RunAsync(
                    validatedTools.Ffmpeg.ExecutablePath,
                    jobDirectory,
                    generationArguments,
                    validatedTools.Ffmpeg.InvocationTimeout,
                    validatedTools.Ffmpeg.MaximumStandardOutputBytes,
                    validatedTools.Ffmpeg.MaximumStandardErrorBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyExecutableIntegrityAsync(
                    validatedTools.Ffmpeg,
                    ffmpegReadLock,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await VerifyCurrentSourceIntegrityAsync(
                layout,
                sourcePath,
                sourceReadLock,
                request.ExpectedSourceSha256,
                request.ExpectedSourceByteLength,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccessfulGeneration(generation);
        EnsureCompletedProgress(generation.StandardOutput);
        var vfrObservation = ParseVfrObservation(generation.StandardError);
        var audioObservation = ParseAudioObservation(generation.StandardError);

        if (!File.Exists(proxyPartPath) || !File.Exists(audioPartPath))
        {
            throw Failure(
                MediaPreprocessingFailure.ArtifactIntegrityInvalid,
                "FFmpeg did not create both required preprocessing artifacts.");
        }

        progress?.Report(new(request.MediaAssetId, MediaPreprocessingPhase.VerifyingArtifacts));
        var proxyProbe = await ProbeArtifactAsync(
                validatedTools.Ffprobe,
                jobDirectory,
                proxyPartPath,
                cancellationToken)
            .ConfigureAwait(false);
        var audioProbe = await ProbeArtifactAsync(
                validatedTools.Ffprobe,
                jobDirectory,
                audioPartPath,
                cancellationToken)
            .ConfigureAwait(false);
        var proxyMetadata = ValidateProxyProbe(proxyProbe);
        var analysisAudioMetadata = ValidateAnalysisAudioProbe(audioProbe);

        File.Move(proxyPartPath, proxyPath);
        File.Move(audioPartPath, audioPath);

        progress?.Report(new(request.MediaAssetId, MediaPreprocessingPhase.HashingArtifacts));
        var proxyArtifact = await HashArtifactAsync(proxyPath, cancellationToken).ConfigureAwait(false);
        var audioArtifact = await HashArtifactAsync(audioPath, cancellationToken).ConfigureAwait(false);
        var mappedDuration = Math.Max(
            proxyMetadata.DurationMicroseconds,
            analysisAudioMetadata.DurationMicroseconds);

        progress?.Report(new(request.MediaAssetId, MediaPreprocessingPhase.WritingManifests));
        var timestampDocument = new TimestampMapDocument(
            1,
            CurrentTimestampMapVersion,
            request.ExpectedSourceSha256,
            proxyArtifact.Sha256,
            audioArtifact.Sha256,
            sourceTimelineOrigin,
            mappedDuration,
            [new(0, sourceTimelineOrigin, 1, 1, ProxyFrameRate, false)],
            [new(0, sourceTimelineOrigin, 1_000_000, AnalysisAudioSampleRate, analysisAudioMetadata.SampleCount)],
            TimestampLimitations);
        await WriteJsonArtifactAsync(timestampMapPath, timestampDocument, cancellationToken)
            .ConfigureAwait(false);
        var timestampArtifact = await HashArtifactAsync(timestampMapPath, cancellationToken)
            .ConfigureAwait(false);

        var preprocessedAtUtc = DateTimeOffset.UtcNow;
        var manifestDocument = new PreprocessingManifestDocument(
            1,
            CurrentPreprocessingContractVersion,
            contractSha256,
            new(request.ExpectedSourceSha256, request.ExpectedSourceByteLength),
            new(
                "proxy.mp4",
                proxyArtifact.Sha256,
                proxyArtifact.ByteLength,
                proxyMetadata.ContainerFormat,
                proxyMetadata.VideoCodec,
                proxyMetadata.PixelFormat,
                proxyMetadata.Width,
                proxyMetadata.Height,
                proxyMetadata.FrameRateNumerator,
                proxyMetadata.FrameRateDenominator,
                proxyMetadata.AudioCodec,
                proxyMetadata.AudioSampleRateHz,
                proxyMetadata.AudioChannelCount,
                proxyMetadata.DurationMicroseconds),
            new(
                "audio.wav",
                audioArtifact.Sha256,
                audioArtifact.ByteLength,
                analysisAudioMetadata.Codec,
                analysisAudioMetadata.SampleRateHz,
                analysisAudioMetadata.ChannelCount,
                analysisAudioMetadata.SampleCount,
                analysisAudioMetadata.DurationMicroseconds),
            new("timestamp-map.json", timestampArtifact.Sha256, timestampArtifact.ByteLength),
            new(
                sourceTimelineOrigin,
                mappedDuration,
                timestampDocument.VideoEntries.Count,
                timestampDocument.AudioSegments.Count,
                vfrObservation,
                audioObservation),
            new(
                preflight.Ffmpeg.Version,
                preflight.Ffmpeg.CompilerIdentifier,
                preflight.Ffmpeg.ConfigurationSha256,
                preflight.Ffmpeg.ExecutableSha256,
                preflight.ValidationContractSha256),
            NotAssessed,
            NotAssessed,
            preprocessedAtUtc,
            TimestampLimitations);
        await WriteJsonArtifactAsync(manifestPath, manifestDocument, cancellationToken)
            .ConfigureAwait(false);
        var manifestArtifact = await HashArtifactAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);

        await VerifyCurrentSourceIntegrityAsync(
                layout,
                sourcePath,
                sourceReadLock,
                request.ExpectedSourceSha256,
                request.ExpectedSourceByteLength,
                cancellationToken)
            .ConfigureAwait(false);

        var intendedRelativeDirectory = NormalizeRelativePath(
            Path.GetRelativePath(layout.WorkspaceRoot, intendedPreparedDirectory));
        var output = new MediaPreprocessingResult(
            request.MediaAssetId,
            request.ExpectedSourceSha256,
            request.ExpectedSourceByteLength,
            CurrentPreprocessingContractVersion,
            contractSha256,
            JoinRelative(intendedRelativeDirectory, "proxy.mp4"),
            proxyArtifact.Sha256,
            proxyArtifact.ByteLength,
            proxyMetadata.ContainerFormat,
            proxyMetadata.VideoCodec,
            proxyMetadata.PixelFormat,
            proxyMetadata.Width,
            proxyMetadata.Height,
            proxyMetadata.FrameRateNumerator,
            proxyMetadata.FrameRateDenominator,
            proxyMetadata.AudioCodec,
            proxyMetadata.AudioSampleRateHz,
            proxyMetadata.AudioChannelCount,
            proxyMetadata.DurationMicroseconds,
            JoinRelative(intendedRelativeDirectory, "audio.wav"),
            audioArtifact.Sha256,
            audioArtifact.ByteLength,
            analysisAudioMetadata.Codec,
            analysisAudioMetadata.SampleRateHz,
            analysisAudioMetadata.ChannelCount,
            analysisAudioMetadata.SampleCount,
            analysisAudioMetadata.DurationMicroseconds,
            JoinRelative(intendedRelativeDirectory, "timestamp-map.json"),
            timestampArtifact.Sha256,
            timestampArtifact.ByteLength,
            JoinRelative(intendedRelativeDirectory, "preprocessing-manifest.json"),
            manifestArtifact.Sha256,
            manifestArtifact.ByteLength,
            sourceTimelineOrigin,
            mappedDuration,
            timestampDocument.VideoEntries.Count,
            timestampDocument.AudioSegments.Count,
            preflight.Ffmpeg.Version,
            preflight.Ffmpeg.CompilerIdentifier,
            preflight.Ffmpeg.ConfigurationSha256,
            preflight.Ffmpeg.ExecutableSha256,
            preflight.ValidationContractSha256,
            NotAssessed,
            NotAssessed,
            preprocessedAtUtc);

        progress?.Report(new(request.MediaAssetId, MediaPreprocessingPhase.Completed));
        return new(
            request.JobId,
            stagedDirectory,
            intendedPreparedDirectory,
            output);
    }

    private async Task<long> ProbeFirstDecodedPtsAsync(
        ValidatedExecutable executable,
        string workingDirectory,
        string sourcePath,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "-v", "error",
            "-protocol_whitelist", "file,pipe",
            "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
            "-read_intervals", "%+#256",
            "-show_frames",
            "-show_entries", "frame=stream_index,best_effort_timestamp_time,pts_time",
            "-of", "json",
            sourcePath,
        ];

        BoundedProcessResult result;
        await using (var readLock = OpenExecutableReadLock(executable.ExecutablePath))
        {
            await VerifyExecutableIntegrityAsync(executable, readLock, cancellationToken)
                .ConfigureAwait(false);
            result = await _processRunner.RunAsync(
                    executable.ExecutablePath,
                    workingDirectory,
                    arguments,
                    executable.InvocationTimeout,
                    executable.MaximumStandardOutputBytes,
                    executable.MaximumStandardErrorBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyExecutableIntegrityAsync(executable, readLock, cancellationToken)
                .ConfigureAwait(false);
        }

        EnsureSuccessfulTimelineProbe(result);
        return ParseFirstDecodedPts(result.StandardOutput, streamIndex);
    }

    private async Task<string> ProbeArtifactAsync(
        ValidatedExecutable executable,
        string workingDirectory,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "-v", "error",
            "-protocol_whitelist", "file,pipe",
            "-show_format",
            "-show_streams",
            "-of", "json",
            artifactPath,
        ];

        BoundedProcessResult result;
        await using (var readLock = OpenExecutableReadLock(executable.ExecutablePath))
        {
            await VerifyExecutableIntegrityAsync(executable, readLock, cancellationToken)
                .ConfigureAwait(false);
            result = await _processRunner.RunAsync(
                    executable.ExecutablePath,
                    workingDirectory,
                    arguments,
                    executable.InvocationTimeout,
                    executable.MaximumStandardOutputBytes,
                    executable.MaximumStandardErrorBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await VerifyExecutableIntegrityAsync(executable, readLock, cancellationToken)
                .ConfigureAwait(false);
        }

        EnsureSuccessfulArtifactProbe(result);
        return result.StandardOutput;
    }

    private static IReadOnlyList<string> BuildGenerationArguments(
        string sourcePath,
        int videoStreamIndex,
        int audioStreamIndex,
        long sourceOriginMicroseconds,
        string proxyPartPath,
        string audioPartPath)
    {
        var origin = sourceOriginMicroseconds.ToString(CultureInfo.InvariantCulture);
        var filter = string.Concat(
            $"[0:{videoStreamIndex.ToString(CultureInfo.InvariantCulture)}]split=2[vbase][vscan];",
            "[vscan]vfrdet[vnull];",
            $"[vbase]setpts=PTS-({origin}/1000000)/TB,",
            "scale=w='min(iw,1280)':h='min(ih,720)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic,",
            "setsar=1,fps=fps=30:round=near:start_time=0,format=pix_fmts=yuv420p[vproxy];",
            $"[0:{audioStreamIndex.ToString(CultureInfo.InvariantCulture)}]asetpts=PTS-({origin}/1000000)/TB,asplit=2[ap0][aw0];",
            "[ap0]aresample=48000:async=1000:first_pts=0,",
            "aformat=sample_rates=48000:sample_fmts=fltp:channel_layouts=stereo[aproxy];",
            "[aw0]aresample=16000:async=1000:first_pts=0,",
            "aformat=sample_rates=16000:sample_fmts=s16:channel_layouts=mono,",
            "astats=metadata=0:reset=0:measure_perchannel=none:",
            "measure_overall=Peak_level+RMS_level+Number_of_samples+Peak_count[awav]");

        return
        [
            "-nostdin", "-hide_banner", "-v", "info", "-xerror",
            "-hwaccel", "none",
            "-protocol_whitelist", "file,pipe",
            "-threads", "1",
            "-stats_period", "5",
            "-progress", "pipe:1",
            "-nostats", "-n", "-copyts",
            "-i", sourcePath,
            "-filter_complex", filter,

            "-map", "[vproxy]", "-map", "[aproxy]",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-sn", "-dn",
            "-c:v", "mpeg4", "-q:v", "5", "-bf", "0", "-g", "120",
            "-threads:v", "1", "-pix_fmt", "yuv420p", "-flags:v", "+bitexact",
            "-c:a", "aac", "-b:a", "128k", "-aac_coder", "twoloop",
            "-threads:a", "1", "-flags:a", "+bitexact",
            "-movflags", "+faststart+disable_chpl", "-fflags", "+bitexact",
            "-f", "mp4", proxyPartPath,

            "-map", "[awav]", "-vn", "-sn", "-dn",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-c:a", "pcm_s16le", "-threads:a", "1", "-flags:a", "+bitexact",
            "-fflags", "+bitexact", "-f", "wav", audioPartPath,

            "-map", "[vnull]", "-an", "-sn", "-dn", "-f", "null", "-",
        ];
    }

    private static long ParseFirstDecodedPts(string json, int streamIndex)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("frames", out var frames)
                || frames.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException();
            }

            foreach (var frame in frames.EnumerateArray())
            {
                if (!TryReadInt32(frame, "stream_index", out var actualIndex)
                    || actualIndex != streamIndex)
                {
                    continue;
                }

                if (TryReadDecimal(frame, "best_effort_timestamp_time", out var seconds)
                    || TryReadDecimal(frame, "pts_time", out seconds))
                {
                    return checked((long)decimal.Round(
                        seconds * 1_000_000m,
                        0,
                        MidpointRounding.AwayFromZero));
                }
            }
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or OverflowException)
        {
            throw Failure(
                MediaPreprocessingFailure.TimelineProbeMalformed,
                "ffprobe returned malformed first-frame timing data.");
        }

        throw Failure(
            MediaPreprocessingFailure.TimelineProbeMalformed,
            "ffprobe did not return a decoded presentation timestamp for a selected stream.");
    }

    private static ProxyMetadata ValidateProxyProbe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var format = RequireObject(root, "format");
            var formatName = RequireString(format, "format_name");
            if (!formatName.Split(',').Any(value => value.Equals("mp4", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArtifactContractException();
            }

            var streams = RequireArray(root, "streams").EnumerateArray().ToArray();
            var videos = streams.Where(stream => ReadOptionalString(stream, "codec_type") == "video").ToArray();
            var audios = streams.Where(stream => ReadOptionalString(stream, "codec_type") == "audio").ToArray();
            if (videos.Length != 1 || audios.Length != 1)
            {
                throw new ArtifactContractException();
            }

            var video = videos[0];
            var audio = audios[0];
            var videoCodec = RequireString(video, "codec_name");
            var pixelFormat = RequireString(video, "pix_fmt");
            var width = RequirePositiveInt32(video, "width");
            var height = RequirePositiveInt32(video, "height");
            var frameRate = ParseRational(RequireString(video, "avg_frame_rate"));
            var audioCodec = RequireString(audio, "codec_name");
            var sampleRate = ParsePositiveInt32(RequireString(audio, "sample_rate"));
            var channels = RequirePositiveInt32(audio, "channels");
            var duration = ParseDurationMicroseconds(format);

            if (videoCodec != "mpeg4"
                || pixelFormat != "yuv420p"
                || width > ProxyMaximumWidth
                || height > ProxyMaximumHeight
                || width % 2 != 0
                || height % 2 != 0
                || frameRate.Numerator != ProxyFrameRate
                || frameRate.Denominator != 1
                || audioCodec != "aac"
                || sampleRate != ProxyAudioSampleRate
                || channels != ProxyAudioChannels)
            {
                throw new ArtifactContractException();
            }

            return new(
                "mp4",
                videoCodec,
                pixelFormat,
                width,
                height,
                frameRate.Numerator,
                frameRate.Denominator,
                audioCodec,
                sampleRate,
                channels,
                duration);
        }
        catch (ArtifactContractException)
        {
            throw Failure(
                MediaPreprocessingFailure.ArtifactContractMismatch,
                "The generated proxy does not match the frozen preprocessing contract.");
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            throw Failure(
                MediaPreprocessingFailure.ArtifactProbeMalformed,
                "ffprobe returned malformed proxy metadata.");
        }
    }

    private static AnalysisAudioMetadata ValidateAnalysisAudioProbe(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var format = RequireObject(root, "format");
            var formatName = RequireString(format, "format_name");
            if (!formatName.Split(',').Any(value => value.Equals("wav", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArtifactContractException();
            }

            var streams = RequireArray(root, "streams").EnumerateArray().ToArray();
            var videos = streams.Where(stream => ReadOptionalString(stream, "codec_type") == "video").ToArray();
            var audios = streams.Where(stream => ReadOptionalString(stream, "codec_type") == "audio").ToArray();
            if (videos.Length != 0 || audios.Length != 1)
            {
                throw new ArtifactContractException();
            }

            var audio = audios[0];
            var codec = RequireString(audio, "codec_name");
            var sampleRate = ParsePositiveInt32(RequireString(audio, "sample_rate"));
            var channels = RequirePositiveInt32(audio, "channels");
            var timeBase = ParseRational(RequireString(audio, "time_base"));
            var sampleCount = RequirePositiveInt64(audio, "duration_ts");
            if (codec != "pcm_s16le"
                || sampleRate != AnalysisAudioSampleRate
                || channels != AnalysisAudioChannels
                || timeBase.Numerator != 1
                || timeBase.Denominator != AnalysisAudioSampleRate)
            {
                throw new ArtifactContractException();
            }

            var duration = checked((long)decimal.Round(
                (decimal)sampleCount * 1_000_000m / sampleRate,
                0,
                MidpointRounding.AwayFromZero));
            return new(codec, sampleRate, channels, sampleCount, duration);
        }
        catch (ArtifactContractException)
        {
            throw Failure(
                MediaPreprocessingFailure.ArtifactContractMismatch,
                "The generated analysis audio does not match the frozen preprocessing contract.");
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            throw Failure(
                MediaPreprocessingFailure.ArtifactProbeMalformed,
                "ffprobe returned malformed analysis-audio metadata.");
        }
    }

    private static void EnsureSuccessfulTimelineProbe(BoundedProcessResult result)
    {
        switch (result.Termination)
        {
            case ProcessTermination.LaunchFailed:
                throw Failure(MediaPreprocessingFailure.TimelineProbeLaunchFailed, "ffprobe could not start.");
            case ProcessTermination.TimedOut:
                throw Failure(MediaPreprocessingFailure.TimelineProbeTimedOut, "Timeline probing timed out.");
            case ProcessTermination.StandardOutputLimitExceeded:
            case ProcessTermination.StandardErrorLimitExceeded:
                throw Failure(
                    MediaPreprocessingFailure.TimelineProbeOutputLimitExceeded,
                    "Timeline probing exceeded its output limit.");
            case ProcessTermination.Exited when result.ExitCode != 0:
                throw Failure(MediaPreprocessingFailure.TimelineProbeFailed, "Timeline probing failed.");
        }
    }

    private static void EnsureSuccessfulGeneration(BoundedProcessResult result)
    {
        switch (result.Termination)
        {
            case ProcessTermination.LaunchFailed:
                throw Failure(MediaPreprocessingFailure.GenerationLaunchFailed, "FFmpeg could not start.");
            case ProcessTermination.TimedOut:
                throw Failure(MediaPreprocessingFailure.GenerationTimedOut, "Media preprocessing timed out.");
            case ProcessTermination.StandardOutputLimitExceeded:
            case ProcessTermination.StandardErrorLimitExceeded:
                throw Failure(
                    MediaPreprocessingFailure.GenerationOutputLimitExceeded,
                    "Media preprocessing exceeded its output limit.");
            case ProcessTermination.Exited when result.ExitCode != 0:
                throw Failure(MediaPreprocessingFailure.GenerationFailed, "FFmpeg could not generate the preprocessing artifacts.");
        }
    }

    private static void EnsureSuccessfulArtifactProbe(BoundedProcessResult result)
    {
        switch (result.Termination)
        {
            case ProcessTermination.LaunchFailed:
                throw Failure(MediaPreprocessingFailure.ArtifactProbeLaunchFailed, "ffprobe could not start.");
            case ProcessTermination.TimedOut:
                throw Failure(MediaPreprocessingFailure.ArtifactProbeTimedOut, "Artifact probing timed out.");
            case ProcessTermination.StandardOutputLimitExceeded:
            case ProcessTermination.StandardErrorLimitExceeded:
                throw Failure(
                    MediaPreprocessingFailure.ArtifactProbeOutputLimitExceeded,
                    "Artifact probing exceeded its output limit.");
            case ProcessTermination.Exited when result.ExitCode != 0:
                throw Failure(MediaPreprocessingFailure.ArtifactProbeFailed, "A generated artifact could not be probed.");
        }
    }

    private static void EnsureCompletedProgress(string output)
    {
        var final = output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .LastOrDefault(line => line.StartsWith("progress=", StringComparison.Ordinal));
        if (!string.Equals(final, "progress=end", StringComparison.Ordinal))
        {
            throw Failure(
                MediaPreprocessingFailure.GenerationProgressMalformed,
                "FFmpeg did not report a completed preprocessing run.");
        }
    }

    private static VfrObservation ParseVfrObservation(string standardError)
    {
        Match? selected = null;
        foreach (Match match in VfrRegex().Matches(standardError))
        {
            selected = match;
        }

        if (selected is null)
        {
            return new(null, 0, 0);
        }

        double? ratio = null;
        if (double.TryParse(
                selected.Groups["ratio"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            && double.IsFinite(parsed))
        {
            ratio = parsed;
        }

        _ = long.TryParse(
            selected.Groups["variable"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var variable);
        _ = long.TryParse(
            selected.Groups["constant"].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var constant);
        return new(ratio, variable, constant);
    }

    private static AudioObservation ParseAudioObservation(string standardError)
    {
        return new(
            ParseLastFiniteMetric(standardError, "Peak level dB:"),
            ParseLastFiniteMetric(standardError, "RMS level dB:"),
            ParseLastFiniteMetric(standardError, "Peak count:"),
            ParseLastInt64Metric(standardError, "Number of samples:"));
    }

    private static double? ParseLastFiniteMetric(string text, string marker)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Reverse())
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var value = line[(index + marker.Length)..].Trim();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static long? ParseLastInt64Metric(string text, string marker)
    {
        var value = ParseLastFiniteMetric(text, marker);
        return value is { } parsed && parsed >= 0 && parsed <= long.MaxValue
            ? checked((long)Math.Round(parsed, MidpointRounding.AwayFromZero))
            : null;
    }

    [GeneratedRegex(@"VFR:(?<ratio>[^\s]+)\s+\((?<variable>\d+)/(?<constant>\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex VfrRegex();

    private static decimal ParseDecimal(JsonElement element, string propertyName)
    {
        if (!TryReadDecimal(element, propertyName, out var result))
        {
            throw new FormatException();
        }

        return result;
    }

    private static bool TryReadDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value),
            JsonValueKind.Number => property.TryGetDecimal(out value),
            _ => false,
        };
    }

    private static bool TryReadInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static JsonElement RequireObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException();
        }

        return property;
    }

    private static JsonElement RequireArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException();
        }

        return property;
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        var result = ReadOptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(result) ? throw new FormatException() : result;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int RequirePositiveInt32(JsonElement element, string propertyName)
    {
        if (!TryReadInt32(element, propertyName, out var value) || value <= 0)
        {
            throw new FormatException();
        }

        return value;
    }

    private static long RequirePositiveInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var value)
            || value <= 0)
        {
            throw new FormatException();
        }

        return value;
    }

    private static int ParsePositiveInt32(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new FormatException();

    private static Rational ParseRational(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator)
            || numerator <= 0
            || denominator <= 0)
        {
            throw new FormatException();
        }

        var divisor = GreatestCommonDivisor(numerator, denominator);
        return new(numerator / divisor, denominator / divisor);
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }

    private static long ParseDurationMicroseconds(JsonElement format)
    {
        var durationSeconds = ParseDecimal(format, "duration");
        if (durationSeconds <= 0)
        {
            throw new FormatException();
        }

        return checked((long)decimal.Round(
            durationSeconds * 1_000_000m,
            0,
            MidpointRounding.AwayFromZero));
    }

    private static async Task WriteJsonArtifactAsync<T>(
        string destinationPath,
        T document,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + ".part";
        if (File.Exists(destinationPath) || File.Exists(temporaryPath))
        {
            throw Failure(
                MediaPreprocessingFailure.ManifestWriteFailed,
                "A preprocessing JSON artifact already exists.");
        }

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        ArtifactJsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw Failure(
                MediaPreprocessingFailure.ManifestWriteFailed,
                "A preprocessing JSON artifact could not be written.");
        }
    }

    private static async Task<ArtifactIntegrity> HashArtifactAsync(
        string path,
        CancellationToken cancellationToken)
    {
        EnsureNotReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length <= 0)
        {
            throw Failure(
                MediaPreprocessingFailure.ArtifactIntegrityInvalid,
                "A generated preprocessing artifact is empty.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new(Convert.ToHexStringLower(digest), info.Length);
    }

    private static string ComputePreprocessingContractSha256(MediaValidationPreflight preflight)
    {
        var canonical = string.Join(
            '\n',
            CurrentPreprocessingContractVersion,
            CurrentTimestampMapVersion,
            "cpu.threads=1",
            "proxy.container=mp4",
            "proxy.video=mpeg4:q5:bf0:g120:yuv420p:1280x720:30/1",
            "proxy.audio=aac:128k:twoloop:48000:stereo",
            "analysis.audio=pcm_s16le:16000:mono",
            "timeline=setpts/asetpts:first-decoded-origin:aresample-async1000",
            "quality=NotAssessed",
            "applicability=NotAssessed",
            $"ffmpeg.version={preflight.Ffmpeg.Version}",
            $"ffmpeg.compiler={preflight.Ffmpeg.CompilerIdentifier}",
            $"ffmpeg.configuration={preflight.Ffmpeg.ConfigurationSha256}",
            $"ffmpeg.sha256={preflight.Ffmpeg.ExecutableSha256}",
            $"validation.contract={preflight.ValidationContractSha256}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string BuildPreparedDirectory(
        ProfileWorkspaceLayout layout,
        string sourcePath,
        string contractSha256)
    {
        var assetDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw Failure(MediaPreprocessingFailure.MediaPathInvalid, "The source media has no asset directory.");
        var preparedRoot = RequireContainedPath(
            assetDirectory,
            Path.Combine(assetDirectory, "Prepared"),
            "The prepared-media root escapes its asset.");
        var contractDirectory = $"v1_{contractSha256[..ContractHashPrefixLength]}";
        return RequireContainedPath(
            preparedRoot,
            Path.Combine(preparedRoot, contractDirectory),
            "The prepared-media contract path escapes its asset.");
    }

    private static string JoinRelative(string directory, string leaf) =>
        NormalizeRelativePath(directory + "/" + leaf);

    private sealed record ValidatedExecutable(
        string ExecutablePath,
        string ExpectedSha256,
        TimeSpan InvocationTimeout,
        int MaximumStandardOutputBytes,
        int MaximumStandardErrorBytes);

    private sealed record ValidatedTools(ValidatedExecutable Ffprobe, ValidatedExecutable Ffmpeg);
    private sealed record ArtifactIntegrity(string Sha256, long ByteLength);
    private sealed record Rational(long Numerator, long Denominator);
    private sealed record ProxyMetadata(
        string ContainerFormat,
        string VideoCodec,
        string PixelFormat,
        int Width,
        int Height,
        long FrameRateNumerator,
        long FrameRateDenominator,
        string AudioCodec,
        int AudioSampleRateHz,
        int AudioChannelCount,
        long DurationMicroseconds);
    private sealed record AnalysisAudioMetadata(
        string Codec,
        int SampleRateHz,
        int ChannelCount,
        long SampleCount,
        long DurationMicroseconds);
    private sealed record VfrObservation(double? Ratio, long VariableIntervals, long ConstantIntervals);
    private sealed record AudioObservation(
        double? PeakLevelDb,
        double? RmsLevelDb,
        double? PeakCount,
        long? NumberOfSamples);
    private sealed record TimestampVideoEntry(
        long TargetStartMicroseconds,
        long SourceStartMicroseconds,
        long ScaleNumerator,
        long ScaleDenominator,
        int TargetFramesPerSecond,
        bool ExactFrameLineage);
    private sealed record TimestampAudioSegment(
        long TargetStartSample,
        long SourceStartMicroseconds,
        long MicrosecondsNumerator,
        long SamplesDenominator,
        long SampleCount);
    private sealed record TimestampMapDocument(
        int SchemaVersion,
        string ContractVersion,
        string SourceSha256,
        string ProxySha256,
        string AnalysisAudioSha256,
        long SourceTimelineOriginMicroseconds,
        long MappedDurationMicroseconds,
        IReadOnlyList<TimestampVideoEntry> VideoEntries,
        IReadOnlyList<TimestampAudioSegment> AudioSegments,
        IReadOnlyList<string> Limitations);
    private sealed record ManifestSource(string Sha256, long ByteLength);
    private sealed record ManifestProxy(
        string FileName,
        string Sha256,
        long ByteLength,
        string ContainerFormat,
        string VideoCodec,
        string PixelFormat,
        int Width,
        int Height,
        long FrameRateNumerator,
        long FrameRateDenominator,
        string AudioCodec,
        int AudioSampleRateHz,
        int AudioChannelCount,
        long DurationMicroseconds);
    private sealed record ManifestAudio(
        string FileName,
        string Sha256,
        long ByteLength,
        string Codec,
        int SampleRateHz,
        int ChannelCount,
        long SampleCount,
        long DurationMicroseconds);
    private sealed record ManifestArtifact(string FileName, string Sha256, long ByteLength);
    private sealed record ManifestTimeline(
        long SourceOriginMicroseconds,
        long MappedDurationMicroseconds,
        int VideoMapEntryCount,
        int AudioMapSegmentCount,
        VfrObservation VariableFrameRateObservation,
        AudioObservation AnalysisAudioObservation);
    private sealed record ManifestTool(
        string Version,
        string CompilerIdentifier,
        string ConfigurationSha256,
        string ExecutableSha256,
        string MediaValidationContractSha256);
    private sealed record PreprocessingManifestDocument(
        int SchemaVersion,
        string PreprocessingContractVersion,
        string PreprocessingContractSha256,
        ManifestSource Source,
        ManifestProxy Proxy,
        ManifestAudio AnalysisAudio,
        ManifestArtifact TimestampMap,
        ManifestTimeline Timeline,
        ManifestTool Ffmpeg,
        string MediaQualityState,
        string ModelApplicabilityState,
        DateTimeOffset PreprocessedAtUtc,
        IReadOnlyList<string> Limitations);
    private sealed class ArtifactContractException : Exception;
}
