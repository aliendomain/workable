namespace Workable;
public enum WorkerState
{
    Queued,
    Running,
    Waiting,
    Retrying,
    Pausing,
    Paused,
    Canceling,
    Canceled,
    Completed,
    Failed,
}
