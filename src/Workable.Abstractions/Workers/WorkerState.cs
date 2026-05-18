namespace Workable;
public enum WorkerState
{
    Queued,
    Running,
    Waiting,
    Retrying,
    Pausing,
    Paused,
    Interrupting,
    Interrupted,
    Canceling,
    Canceled,
    Completed,
    Failed,
}
