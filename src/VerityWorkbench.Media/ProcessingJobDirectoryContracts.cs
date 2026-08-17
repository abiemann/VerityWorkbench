namespace VerityWorkbench.Media;

public enum ProcessingJobDirectoryState
{
    Present,
    Missing,
}

public sealed record ProcessingJobDirectoryInspection(
    ProcessingJobDirectoryState State,
    string? FullPath)
{
    public bool IsPresent => State == ProcessingJobDirectoryState.Present;
}

public enum ProcessingJobDirectoryFailure
{
    WorkspaceInvalid,
    JobIdInvalid,
    PathInvalid,
    JobIdMismatch,
    TargetNotDirectory,
    MarkerInvalid,
    ReparsePointDetected,
    PendingPromotionEvidence,
    Missing,
}

public sealed class ProcessingJobDirectoryException : Exception
{
    public ProcessingJobDirectoryException(
        ProcessingJobDirectoryFailure failure,
        string message)
        : base(message)
    {
        Failure = failure;
    }

    public ProcessingJobDirectoryFailure Failure { get; }
}
