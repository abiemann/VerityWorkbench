using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VerityWorkbench.Core.Workspaces;

namespace VerityWorkbench.Media;

/// <summary>
/// Computes deterministic, label-blind integer facts over the complete verified
/// mono 16 kHz signed 16-bit PCM analysis WAV. It makes no quality, speech,
/// language, identity, authenticity, or behavioral judgment.
/// </summary>
public sealed class AudioPcmObservationService
{
    public const string CurrentObservationContractVersion =
        "verityworkbench.audio-pcm-observation.v1";

    public static readonly string CurrentObservationContractSha256 =
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(
                '\n',
                CurrentObservationContractVersion,
                "input=verified-prepared-bundle:audio.wav",
                "wave=RIFF/WAVE:format-tag-1:pcm-s16le:16000-hz:mono",
                "scope=complete-data-chunk:sample-zero-through-end",
                "facts=min,max,absolute-peak,positive,negative,zero,+32767,-32768,adjacent-opposite-sign-crossings,sum,squared-sum",
                "crossing=directly-adjacent-nonzero-samples-with-opposite-sign",
                "accumulation=exact-arbitrary-precision-integer",
                "labels=excluded",
                "thresholds-and-judgments=none"))));

    public const string SampleEncoding = "pcm_s16le";
    public const int RequiredWaveFormatTag = 1;
    public const int RequiredSampleRateHz = 16_000;
    public const int RequiredChannelCount = 1;
    public const int RequiredBitsPerSample = 16;
    public const int RequiredBlockAlignBytes = 2;
    public const int RequiredByteRate = 32_000;

    private const int ReadBufferSize = 128 * 1024;
    private readonly MediaPreprocessingService _preprocessingService;

    public AudioPcmObservationService()
        : this(new MediaPreprocessingService())
    {
    }

    internal AudioPcmObservationService(MediaPreprocessingService preprocessingService)
    {
        _preprocessingService = preprocessingService
            ?? throw new ArgumentNullException(nameof(preprocessingService));
    }

    public async Task<AudioPcmObservationResult> ObserveAsync(
        ProfileWorkspaceLayout layout,
        MediaPreprocessingResult committed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(committed);

        PreparedMediaAnalysisAudioOpenResult opened;
        try
        {
            opened = await _preprocessingService.OpenVerifiedAnalysisAudioAsync(
                    layout,
                    committed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MediaPreprocessingException exception) when (
            exception.Failure == MediaPreprocessingFailure.WorkspaceInvalid)
        {
            throw Failure(
                AudioPcmObservationFailure.WorkspaceInvalid,
                "The profile workspace is invalid or is not initialized.");
        }

        if (!opened.IsOpen)
        {
            throw opened.State switch
            {
                MediaPreparedVerificationState.IntegrityMismatch => Failure(
                    AudioPcmObservationFailure.PreparedIntegrityMismatch,
                    opened.FailureReason
                        ?? "The committed prepared-media bundle failed integrity verification."),
                MediaPreparedVerificationState.OperationalFailure => Failure(
                    AudioPcmObservationFailure.PreparedOperationalFailure,
                    opened.FailureReason
                        ?? "The committed prepared-media bundle could not be read."),
                _ => Failure(
                    AudioPcmObservationFailure.PreparedOperationalFailure,
                    "The committed analysis audio could not be opened."),
            };
        }

        await using var lease = opened.Lease!;
        ValidateCommittedMetadata(committed);

        WaveObservation observation;
        try
        {
            observation = await ObserveWaveAsync(
                    lease.Stream,
                    committed.AnalysisAudioSampleCount,
                    committed.AnalysisAudioDurationMicroseconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WaveMalformedException)
        {
            throw Failure(
                AudioPcmObservationFailure.WaveMalformed,
                "The verified analysis WAV has a malformed or unsupported RIFF/WAVE structure.");
        }
        catch (WaveContractException)
        {
            throw Failure(
                AudioPcmObservationFailure.WaveContractMismatch,
                "The verified analysis WAV does not match the frozen PCM observation contract or committed metadata.");
        }
        catch (EndOfStreamException)
        {
            throw Failure(
                AudioPcmObservationFailure.WaveMalformed,
                "The verified analysis WAV ended before its declared RIFF/WAVE structure was complete.");
        }
        catch (IOException)
        {
            throw Failure(
                AudioPcmObservationFailure.PreparedOperationalFailure,
                "The verified analysis WAV could not be read. Its integrity state was not changed.");
        }

        return new(
            committed.MediaAssetId,
            CurrentObservationContractVersion,
            CurrentObservationContractSha256,
            committed.SourceSha256,
            committed.SourceByteLength,
            committed.PreprocessingContractVersion,
            committed.PreprocessingContractSha256,
            committed.AnalysisAudioSha256,
            committed.AnalysisAudioByteLength,
            RequiredWaveFormatTag,
            SampleEncoding,
            RequiredSampleRateHz,
            RequiredChannelCount,
            RequiredBitsPerSample,
            RequiredBlockAlignBytes,
            RequiredByteRate,
            committed.AnalysisAudioSampleCount,
            observation.ProcessedSampleCount,
            observation.DurationMicroseconds,
            observation.MinimumSample,
            observation.MaximumSample,
            observation.AbsolutePeakSample,
            observation.PositiveSampleCount,
            observation.NegativeSampleCount,
            observation.ZeroSampleCount,
            observation.PositiveFullScaleSampleCount,
            observation.NegativeFullScaleSampleCount,
            observation.AdjacentOppositeSignCrossingCount,
            observation.SampleSum.ToString(CultureInfo.InvariantCulture),
            observation.SquaredSampleSum.ToString(CultureInfo.InvariantCulture));
    }

    private static void ValidateCommittedMetadata(MediaPreprocessingResult committed)
    {
        if (committed.MediaAssetId == Guid.Empty
            || !string.Equals(
                committed.PreprocessingContractVersion,
                MediaPreprocessingService.CurrentPreprocessingContractVersion,
                StringComparison.Ordinal)
            || !IsLowercaseSha256(committed.SourceSha256)
            || committed.SourceByteLength <= 0
            || !IsLowercaseSha256(committed.PreprocessingContractSha256)
            || !IsLowercaseSha256(committed.AnalysisAudioSha256)
            || committed.AnalysisAudioByteLength <= 0
            || !string.Equals(committed.AnalysisAudioCodec, SampleEncoding, StringComparison.Ordinal)
            || committed.AnalysisAudioSampleRateHz != RequiredSampleRateHz
            || committed.AnalysisAudioChannelCount != RequiredChannelCount
            || committed.AnalysisAudioSampleCount <= 0
            || committed.AnalysisAudioDurationMicroseconds <= 0)
        {
            throw Failure(
                AudioPcmObservationFailure.PreparedMetadataMismatch,
                "The committed analysis-audio metadata does not match the frozen PCM observation contract.");
        }

        var expectedDuration = ComputeDurationMicroseconds(committed.AnalysisAudioSampleCount);
        if (expectedDuration != committed.AnalysisAudioDurationMicroseconds)
        {
            throw Failure(
                AudioPcmObservationFailure.PreparedMetadataMismatch,
                "The committed analysis-audio sample count and duration are inconsistent.");
        }
    }

    private static async Task<WaveObservation> ObserveWaveAsync(
        Stream stream,
        long committedSampleCount,
        long committedDurationMicroseconds,
        CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek || stream.Length < 12)
        {
            throw new WaveMalformedException();
        }

        stream.Position = 0;
        var header = new byte[12];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new WaveMalformedException();
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        var riffEnd = checked(8L + riffSize);
        if (riffEnd != stream.Length)
        {
            throw new WaveMalformedException();
        }

        var formatSeen = false;
        var dataSeen = false;
        PcmAccumulator? accumulator = null;
        while (stream.Position < riffEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (riffEnd - stream.Position < 8)
            {
                throw new WaveMalformedException();
            }

            var chunkHeader = new byte[8];
            await stream.ReadExactlyAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            var paddedChunkSize = checked((long)chunkSize + (chunkSize & 1u));
            if (paddedChunkSize > riffEnd - stream.Position)
            {
                throw new WaveMalformedException();
            }

            if (chunkHeader.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (formatSeen || dataSeen)
                {
                    throw new WaveMalformedException();
                }

                await ValidateFormatChunkAsync(stream, chunkSize, cancellationToken)
                    .ConfigureAwait(false);
                formatSeen = true;
            }
            else if (chunkHeader.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (!formatSeen || dataSeen || chunkSize == 0 || (chunkSize & 1u) != 0)
                {
                    throw new WaveMalformedException();
                }

                accumulator = await AccumulatePcmAsync(stream, chunkSize, cancellationToken)
                    .ConfigureAwait(false);
                dataSeen = true;
            }
            else
            {
                stream.Seek(chunkSize, SeekOrigin.Current);
            }

            if ((chunkSize & 1u) != 0)
            {
                var padding = new byte[1];
                await stream.ReadExactlyAsync(padding, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!formatSeen || !dataSeen || accumulator is null || stream.Position != riffEnd)
        {
            throw new WaveMalformedException();
        }

        if (accumulator.ProcessedSampleCount != committedSampleCount)
        {
            throw new WaveContractException();
        }

        var durationMicroseconds = ComputeDurationMicroseconds(accumulator.ProcessedSampleCount);
        if (durationMicroseconds != committedDurationMicroseconds)
        {
            throw new WaveContractException();
        }

        return accumulator.ToObservation(durationMicroseconds);
    }

    private static async Task ValidateFormatChunkAsync(
        Stream stream,
        uint chunkSize,
        CancellationToken cancellationToken)
    {
        if (chunkSize < 16)
        {
            throw new WaveMalformedException();
        }

        var format = new byte[16];
        await stream.ReadExactlyAsync(format, cancellationToken).ConfigureAwait(false);
        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0, 2));
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, 2));
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, 4));
        var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8, 4));
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(12, 2));
        var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, 2));
        if (formatTag != RequiredWaveFormatTag
            || channels != RequiredChannelCount
            || sampleRate != RequiredSampleRateHz
            || byteRate != RequiredByteRate
            || blockAlign != RequiredBlockAlignBytes
            || bitsPerSample != RequiredBitsPerSample)
        {
            throw new WaveContractException();
        }

        if (chunkSize > format.Length)
        {
            stream.Seek(chunkSize - format.Length, SeekOrigin.Current);
        }
    }

    private static async Task<PcmAccumulator> AccumulatePcmAsync(
        Stream stream,
        uint dataByteLength,
        CancellationToken cancellationToken)
    {
        var accumulator = new PcmAccumulator();
        var buffer = new byte[ReadBufferSize];
        long remaining = dataByteLength;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, remaining);
            if ((count & 1) != 0)
            {
                count--;
            }

            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
            for (var offset = 0; offset < count; offset += 2)
            {
                accumulator.Add(BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)));
            }

            remaining -= count;
        }

        return accumulator;
    }

    private static long ComputeDurationMicroseconds(long sampleCount) =>
        checked((long)decimal.Round(
            (decimal)sampleCount * 1_000_000m / RequiredSampleRateHz,
            0,
            MidpointRounding.AwayFromZero));

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AudioPcmObservationException Failure(
        AudioPcmObservationFailure failure,
        string message) => new(failure, message);

    private sealed class PcmAccumulator
    {
        private short? _previousSample;

        public long ProcessedSampleCount { get; private set; }
        public int MinimumSample { get; private set; } = int.MaxValue;
        public int MaximumSample { get; private set; } = int.MinValue;
        public int AbsolutePeakSample { get; private set; }
        public long PositiveSampleCount { get; private set; }
        public long NegativeSampleCount { get; private set; }
        public long ZeroSampleCount { get; private set; }
        public long PositiveFullScaleSampleCount { get; private set; }
        public long NegativeFullScaleSampleCount { get; private set; }
        public long AdjacentOppositeSignCrossingCount { get; private set; }
        public BigInteger SampleSum { get; private set; }
        public BigInteger SquaredSampleSum { get; private set; }

        public void Add(short sample)
        {
            checked
            {
                ProcessedSampleCount++;
                if (sample > 0)
                {
                    PositiveSampleCount++;
                }
                else if (sample < 0)
                {
                    NegativeSampleCount++;
                }
                else
                {
                    ZeroSampleCount++;
                }

                if (sample == short.MaxValue)
                {
                    PositiveFullScaleSampleCount++;
                }

                if (sample == short.MinValue)
                {
                    NegativeFullScaleSampleCount++;
                }

                if (_previousSample is { } previous
                    && previous != 0
                    && sample != 0
                    && (previous < 0) != (sample < 0))
                {
                    AdjacentOppositeSignCrossingCount++;
                }
            }

            MinimumSample = Math.Min(MinimumSample, sample);
            MaximumSample = Math.Max(MaximumSample, sample);
            AbsolutePeakSample = Math.Max(
                AbsolutePeakSample,
                sample < 0 ? -(int)sample : sample);
            SampleSum += sample;
            SquaredSampleSum += (BigInteger)sample * sample;
            _previousSample = sample;
        }

        public WaveObservation ToObservation(long durationMicroseconds)
        {
            if (ProcessedSampleCount <= 0)
            {
                throw new WaveMalformedException();
            }

            return new(
                ProcessedSampleCount,
                durationMicroseconds,
                MinimumSample,
                MaximumSample,
                AbsolutePeakSample,
                PositiveSampleCount,
                NegativeSampleCount,
                ZeroSampleCount,
                PositiveFullScaleSampleCount,
                NegativeFullScaleSampleCount,
                AdjacentOppositeSignCrossingCount,
                SampleSum,
                SquaredSampleSum);
        }
    }

    private sealed record WaveObservation(
        long ProcessedSampleCount,
        long DurationMicroseconds,
        int MinimumSample,
        int MaximumSample,
        int AbsolutePeakSample,
        long PositiveSampleCount,
        long NegativeSampleCount,
        long ZeroSampleCount,
        long PositiveFullScaleSampleCount,
        long NegativeFullScaleSampleCount,
        long AdjacentOppositeSignCrossingCount,
        BigInteger SampleSum,
        BigInteger SquaredSampleSum);

    private sealed class WaveMalformedException : Exception;
    private sealed class WaveContractException : Exception;
}
