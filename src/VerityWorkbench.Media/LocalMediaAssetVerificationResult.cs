namespace VerityWorkbench.Media;

public enum LocalMediaAssetVerificationState
{
    Verified,
    IntegrityMismatch,
    OperationalFailure,
}

public sealed record LocalMediaAssetVerificationResult(
    LocalMediaAssetVerificationState State,
    string? FailureReason)
{
    public bool IsValid => State == LocalMediaAssetVerificationState.Verified;
}

/// <summary>
/// Marks a deliberate workspace-boundary or path-shape rejection while remaining
/// compatible with callers that already treat path failures as I/O failures.
/// </summary>
internal sealed class MediaIntegrityException : IOException
{
    public MediaIntegrityException(string message)
        : base(message)
    {
    }

    public MediaIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
