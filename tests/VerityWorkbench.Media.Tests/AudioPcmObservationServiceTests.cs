using System.Security.Cryptography;
using System.Text;

namespace VerityWorkbench.Media.Tests;

public sealed class AudioPcmObservationServiceTests
{
    [Fact]
    public async Task ObserveReturnsExactWholeFileIntegerFacts()
    {
        short[] samples =
        [
            short.MinValue,
            -1,
            0,
            1,
            short.MaxValue,
            1,
            -1,
            0,
            short.MinValue,
            short.MaxValue,
        ];
        using var test = new ObservationTestBundle(samples);

        var result = await new AudioPcmObservationService().ObserveAsync(
            test.Workspace.Layout,
            test.Committed);

        Assert.Equal(test.AssetId, result.MediaAssetId);
        Assert.Equal(AudioPcmObservationService.CurrentObservationContractVersion, result.ObservationContractVersion);
        Assert.Equal(AudioPcmObservationService.CurrentObservationContractSha256, result.ObservationContractSha256);
        Assert.Equal(test.Committed.SourceSha256, result.SourceSha256);
        Assert.Equal(test.Committed.PreprocessingContractSha256, result.PreprocessingContractSha256);
        Assert.Equal(test.Committed.AnalysisAudioSha256, result.AnalysisAudioSha256);
        Assert.Equal(1, result.WaveFormatTag);
        Assert.Equal("pcm_s16le", result.SampleEncoding);
        Assert.Equal(16_000, result.SampleRateHz);
        Assert.Equal(1, result.ChannelCount);
        Assert.Equal(16, result.BitsPerSample);
        Assert.Equal(2, result.BlockAlignBytes);
        Assert.Equal(32_000, result.ByteRate);
        Assert.Equal(samples.LongLength, result.CommittedSampleCount);
        Assert.Equal(samples.LongLength, result.ProcessedSampleCount);
        Assert.Equal(625, result.DurationMicroseconds);
        Assert.Equal(short.MinValue, result.MinimumSample);
        Assert.Equal(short.MaxValue, result.MaximumSample);
        Assert.Equal(32_768, result.AbsolutePeakSample);
        Assert.Equal(4, result.PositiveSampleCount);
        Assert.Equal(4, result.NegativeSampleCount);
        Assert.Equal(2, result.ZeroSampleCount);
        Assert.Equal(2, result.PositiveFullScaleSampleCount);
        Assert.Equal(2, result.NegativeFullScaleSampleCount);
        Assert.Equal(2, result.AdjacentOppositeSignCrossingCount);
        Assert.Equal("-2", result.SampleSum);
        Assert.Equal("4294836230", result.SquaredSampleSum);
    }

    [Fact]
    public async Task ObserveReturnsExactFactsForAllZeroPcm()
    {
        short[] samples = new short[8];
        using var test = new ObservationTestBundle(samples);

        var result = await new AudioPcmObservationService().ObserveAsync(
            test.Workspace.Layout,
            test.Committed);

        Assert.Equal(samples.LongLength, result.CommittedSampleCount);
        Assert.Equal(samples.LongLength, result.ProcessedSampleCount);
        Assert.Equal(500, result.DurationMicroseconds);
        Assert.Equal(0, result.MinimumSample);
        Assert.Equal(0, result.MaximumSample);
        Assert.Equal(0, result.AbsolutePeakSample);
        Assert.Equal(0, result.PositiveSampleCount);
        Assert.Equal(0, result.NegativeSampleCount);
        Assert.Equal(samples.LongLength, result.ZeroSampleCount);
        Assert.Equal(0, result.PositiveFullScaleSampleCount);
        Assert.Equal(0, result.NegativeFullScaleSampleCount);
        Assert.Equal(0, result.AdjacentOppositeSignCrossingCount);
        Assert.Equal("0", result.SampleSum);
        Assert.Equal("0", result.SquaredSampleSum);
    }

    [Fact]
    public async Task ObserveIsDeterministicAndIgnoresPaddedUnknownWaveChunks()
    {
        using var test = new ObservationTestBundle([-2, 2, 0, -3, 3], includeOddJunkChunk: true);
        var service = new AudioPcmObservationService();

        var first = await service.ObserveAsync(test.Workspace.Layout, test.Committed);
        var second = await service.ObserveAsync(test.Workspace.Layout, test.Committed);

        Assert.Equal(first, second);
        Assert.Equal(2, first.AdjacentOppositeSignCrossingCount);
        Assert.Equal("0", first.SampleSum);
        Assert.Equal("26", first.SquaredSampleSum);
        AssertLowercaseSha256(first.ObservationContractSha256);
    }

    [Fact]
    public async Task ObserveRejectsTamperedSiblingBeforeReadingAudioFacts()
    {
        using var test = new ObservationTestBundle([1, -1]);
        await File.WriteAllTextAsync(test.ProxyPath, "pr0xy");

        var exception = await Assert.ThrowsAsync<AudioPcmObservationException>(() =>
            new AudioPcmObservationService().ObserveAsync(
                test.Workspace.Layout,
                test.Committed));

        Assert.Equal(AudioPcmObservationFailure.PreparedIntegrityMismatch, exception.Failure);
        Assert.DoesNotContain(test.Workspace.Root, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ObserveRejectsWaveFormatOutsideFrozenPcmContract()
    {
        using var test = new ObservationTestBundle([1, -1], waveSampleRateHz: 8_000);

        var exception = await Assert.ThrowsAsync<AudioPcmObservationException>(() =>
            new AudioPcmObservationService().ObserveAsync(
                test.Workspace.Layout,
                test.Committed));

        Assert.Equal(AudioPcmObservationFailure.WaveContractMismatch, exception.Failure);
    }

    [Fact]
    public async Task ObserveRejectsWaveSampleCountThatDiffersFromCommittedMetadata()
    {
        using var test = new ObservationTestBundle(
            [1, -1],
            committedSampleCount: 3,
            committedDurationMicroseconds: 188);

        var exception = await Assert.ThrowsAsync<AudioPcmObservationException>(() =>
            new AudioPcmObservationService().ObserveAsync(
                test.Workspace.Layout,
                test.Committed));

        Assert.Equal(AudioPcmObservationFailure.WaveContractMismatch, exception.Failure);
    }

    [Fact]
    public async Task ObserveRejectsInconsistentCommittedSampleDuration()
    {
        using var test = new ObservationTestBundle(
            [1, -1],
            committedDurationMicroseconds: 126);

        var exception = await Assert.ThrowsAsync<AudioPcmObservationException>(() =>
            new AudioPcmObservationService().ObserveAsync(
                test.Workspace.Layout,
                test.Committed));

        Assert.Equal(AudioPcmObservationFailure.PreparedMetadataMismatch, exception.Failure);
    }

    [Fact]
    public async Task ObserveRejectsMalformedRiffLength()
    {
        using var test = new ObservationTestBundle([1, -1], corruptRiffLength: true);

        var exception = await Assert.ThrowsAsync<AudioPcmObservationException>(() =>
            new AudioPcmObservationService().ObserveAsync(
                test.Workspace.Layout,
                test.Committed));

        Assert.Equal(AudioPcmObservationFailure.WaveMalformed, exception.Failure);
    }

    [Fact]
    public async Task OpenVerifiedAnalysisAudioHoldsExactReadHandleUntilDisposed()
    {
        using var test = new ObservationTestBundle([1, -1]);
        var service = new MediaPreprocessingService();

        var opened = await service.OpenVerifiedAnalysisAudioAsync(
            test.Workspace.Layout,
            test.Committed);

        Assert.True(opened.IsOpen);
        var lease = Assert.IsType<PreparedMediaAnalysisAudioLease>(opened.Lease);
        Assert.Equal(0, lease.Stream.Position);
        var signature = new byte[4];
        await lease.Stream.ReadExactlyAsync(signature);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(signature));
        Assert.Throws<IOException>(() =>
            File.Open(test.AudioPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => File.Delete(test.AudioPath));
        }

        await lease.DisposeAsync();
        using (File.Open(
                   test.AudioPath,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.ReadWrite))
        {
        }
    }

    [Fact]
    public async Task CancellationReleasesAnalysisAudioHandle()
    {
        using var test = new ObservationTestBundle(Enumerable.Repeat((short)1, 65_536).ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AudioPcmObservationService().ObserveAsync(
                test.Workspace.Layout,
                test.Committed,
                cancellation.Token));

        using (File.Open(
                   test.AudioPath,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.ReadWrite))
        {
        }
    }

    [Fact]
    public void PublicObservationContractContainsNoLabelPathClockOrJudgmentFields()
    {
        string[] forbiddenTerms =
        [
            "Path",
            "FileName",
            "Label",
            "Condition",
            "Truth",
            "Deception",
            "Quality",
            "Language",
            "Identity",
            "Authenticity",
            "Score",
            "Percent",
            "Threshold",
            "Timestamp",
            "Clock",
        ];

        var propertyNames = typeof(AudioPcmObservationResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.All(
            forbiddenTerms,
            term => Assert.DoesNotContain(
                propertyNames,
                property => property.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertLowercaseSha256(string value)
    {
        Assert.Equal(64, value.Length);
        Assert.All(value, character =>
            Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    private sealed class ObservationTestBundle : IDisposable
    {
        public ObservationTestBundle(
            short[] samples,
            int waveSampleRateHz = 16_000,
            bool includeOddJunkChunk = false,
            bool corruptRiffLength = false,
            long? committedSampleCount = null,
            long? committedDurationMicroseconds = null)
        {
            Workspace = new TestWorkspace();
            AssetId = Guid.NewGuid();
            var contractHash = HashText("pcm-observation-preprocessing-contract");
            var assetDirectory = Path.Combine(
                Workspace.Layout.MediaRoot,
                "asset_" + AssetId.ToString("N")[..12]);
            PreparedDirectory = Path.Combine(
                assetDirectory,
                "Prepared",
                "v1_" + contractHash[..12]);
            Directory.CreateDirectory(PreparedDirectory);

            var waveBytes = CreateWave(
                samples,
                waveSampleRateHz,
                includeOddJunkChunk,
                corruptRiffLength);
            var proxy = WriteArtifact(PreparedDirectory, "proxy.mp4", Encoding.UTF8.GetBytes("proxy"));
            var audio = WriteArtifact(PreparedDirectory, "audio.wav", waveBytes);
            var map = WriteArtifact(PreparedDirectory, "timestamp-map.json", Encoding.UTF8.GetBytes("map"));
            var manifest = WriteArtifact(
                PreparedDirectory,
                "preprocessing-manifest.json",
                Encoding.UTF8.GetBytes("manifest"));
            var preparedRelative = Path.GetRelativePath(
                    Workspace.Layout.WorkspaceRoot,
                    PreparedDirectory)
                .Replace('\\', '/');
            var recordedSampleCount = committedSampleCount ?? samples.LongLength;
            var recordedDuration = committedDurationMicroseconds
                ?? ComputeDurationMicroseconds(recordedSampleCount);

            Committed = new MediaPreprocessingResult(
                MediaAssetId: AssetId,
                SourceSha256: HashText("source"),
                SourceByteLength: 1,
                PreprocessingContractVersion: MediaPreprocessingService.CurrentPreprocessingContractVersion,
                PreprocessingContractSha256: contractHash,
                ProxyWorkspaceRelativePath: preparedRelative + "/proxy.mp4",
                ProxySha256: proxy.Sha256,
                ProxyByteLength: proxy.ByteLength,
                ProxyContainerFormat: "mp4",
                ProxyVideoCodec: "mpeg4",
                ProxyPixelFormat: "yuv420p",
                ProxyWidth: 320,
                ProxyHeight: 240,
                ProxyFrameRateNumerator: 30,
                ProxyFrameRateDenominator: 1,
                ProxyAudioCodec: "aac",
                ProxyAudioSampleRateHz: 48_000,
                ProxyAudioChannelCount: 2,
                ProxyDurationMicroseconds: recordedDuration,
                AnalysisAudioWorkspaceRelativePath: preparedRelative + "/audio.wav",
                AnalysisAudioSha256: audio.Sha256,
                AnalysisAudioByteLength: audio.ByteLength,
                AnalysisAudioCodec: "pcm_s16le",
                AnalysisAudioSampleRateHz: 16_000,
                AnalysisAudioChannelCount: 1,
                AnalysisAudioSampleCount: recordedSampleCount,
                AnalysisAudioDurationMicroseconds: recordedDuration,
                TimestampMapWorkspaceRelativePath: preparedRelative + "/timestamp-map.json",
                TimestampMapSha256: map.Sha256,
                TimestampMapByteLength: map.ByteLength,
                ManifestWorkspaceRelativePath: preparedRelative + "/preprocessing-manifest.json",
                ManifestSha256: manifest.Sha256,
                ManifestByteLength: manifest.ByteLength,
                SourceTimelineOriginMicroseconds: 0,
                MappedDurationMicroseconds: recordedDuration,
                VideoMapEntryCount: 1,
                AudioMapSegmentCount: 1,
                FfmpegVersion: "n8.1-test",
                FfmpegCompilerIdentifier: "compiler",
                FfmpegConfigurationSha256: HashText("configuration"),
                FfmpegExecutableSha256: HashText("ffmpeg"),
                MediaValidationContractSha256: HashText("validation"),
                MediaQualityState: MediaPreprocessingService.NotAssessed,
                ModelApplicabilityState: MediaPreprocessingService.NotAssessed,
                PreprocessedAtUtc: DateTimeOffset.UnixEpoch);

            AudioPath = Path.Combine(PreparedDirectory, "audio.wav");
            ProxyPath = Path.Combine(PreparedDirectory, "proxy.mp4");
        }

        public TestWorkspace Workspace { get; }
        public Guid AssetId { get; }
        public string PreparedDirectory { get; }
        public string AudioPath { get; }
        public string ProxyPath { get; }
        public MediaPreprocessingResult Committed { get; }

        public void Dispose() => Workspace.Dispose();

        private static byte[] CreateWave(
            IReadOnlyList<short> samples,
            int sampleRateHz,
            bool includeOddJunkChunk,
            bool corruptRiffLength)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(0u);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                if (includeOddJunkChunk)
                {
                    writer.Write(Encoding.ASCII.GetBytes("JUNK"));
                    writer.Write(3u);
                    writer.Write(new byte[] { 7, 8, 9 });
                    writer.Write((byte)0);
                }

                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16u);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write((uint)sampleRateHz);
                writer.Write((uint)(sampleRateHz * 2));
                writer.Write((ushort)2);
                writer.Write((ushort)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(checked((uint)samples.Count * 2u));
                foreach (var sample in samples)
                {
                    writer.Write(sample);
                }
            }

            var result = stream.ToArray();
            var riffSize = checked((uint)(result.Length - 8));
            if (corruptRiffLength)
            {
                riffSize--;
            }

            BitConverter.GetBytes(riffSize).CopyTo(result, 4);
            return result;
        }

        private static (string Sha256, long ByteLength) WriteArtifact(
            string directory,
            string fileName,
            byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(directory, fileName), bytes);
            return (Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength);
        }

        private static long ComputeDurationMicroseconds(long sampleCount) =>
            checked((long)decimal.Round(
                (decimal)sampleCount * 1_000_000m / 16_000m,
                0,
                MidpointRounding.AwayFromZero));

        private static string HashText(string value) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
