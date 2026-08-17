namespace VerityWorkbench.Media;

/// <summary>
/// Owns the exact read-only analysis-audio handle whose bytes were verified.
/// The handle denies write and delete sharing until the consumer disposes the lease.
/// </summary>
public sealed class PreparedMediaAnalysisAudioLease : IDisposable, IAsyncDisposable
{
    private FileStream? _stream;

    internal PreparedMediaAnalysisAudioLease(FileStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public Stream Stream => _stream
        ?? throw new ObjectDisposedException(nameof(PreparedMediaAnalysisAudioLease));

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();

    public async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed record PreparedMediaAnalysisAudioOpenResult(
    MediaPreparedVerificationState State,
    PreparedMediaAnalysisAudioLease? Lease,
    string? FailureReason)
{
    public bool IsOpen => State == MediaPreparedVerificationState.Verified && Lease is not null;
}
