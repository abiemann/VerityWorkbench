namespace VerityWorkbench.Data.Profiles;

public enum ProcessingJobState
{
    Queued,
    Running,
    Completed,
    Cancelled,
    Failed,
    Interrupted,
}
