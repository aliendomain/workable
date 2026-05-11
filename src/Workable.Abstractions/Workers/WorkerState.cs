namespace Workable;
public enum WorkerState
{
    Queued,
    Running,
    Waiting,
    Pausing,
    Paused,
    Canceling,
    Canceled,
    Completed,
    Failed,
}
