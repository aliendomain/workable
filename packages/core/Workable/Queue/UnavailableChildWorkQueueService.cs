namespace Workable;

internal sealed class UnavailableChildWorkQueueService : IChildWorkQueueService
{
    internal static UnavailableChildWorkQueueService Instance { get; } = new();

    private UnavailableChildWorkQueueService()
    {
    }

    public Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IWorkerHandle>(Rejected());

    public Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IWorkerHandle>(Rejected());

    private static IWorkerHandle Rejected()
        => WorkerHandle.Rejected(
            WorkQueueOutcome.Invalid(
                [WorkMessage.Error(
                    "workable.child_execution.unavailable",
                    "Child execution is unavailable outside an active Workable worker context.",
                    "child.execution")]));
}
