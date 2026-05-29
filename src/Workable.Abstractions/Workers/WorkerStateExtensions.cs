namespace Workable;

public static class WorkerStateExtensions
{
    public static bool IsFinal(this WorkerState state)
        => state is WorkerState.Canceled or WorkerState.Completed;
}
