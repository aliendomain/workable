namespace Workable;

internal sealed class ChildWorkQueueService(
    WorkQueueService inner,
    WorkerRecord parent) : IChildWorkQueueService
{
    private int revoked;

    public Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Volatile.Read(ref this.revoked) == 1)
        {
            return Task.FromResult<IWorkerHandle>(inner.Reject(Expired(parent.Work.Definition.Name)));
        }

        if (!parent.Work.Definition.Configuration.ChildExecution.Allows(name))
        {
            return Task.FromResult<IWorkerHandle>(inner.Reject(NotDeclared(parent.Work.Definition.Name, name)));
        }

        return inner.EnqueueDelegated(
            name,
            input,
            options,
            parent.RequestContext,
            cancellationToken);
    }

    public Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Enqueue(name, ToWorkInput(input), options, cancellationToken);

    internal void Revoke()
        => Interlocked.Exchange(ref this.revoked, 1);

    private static WorkQueueOutcome NotDeclared(string parentName, string childName)
        => WorkQueueOutcome.Invalid(
            [WorkMessage.Error(
                "workable.child_execution.not_declared",
                $"Work '{parentName}' is not configured to execute '{childName}' as a child.",
                "child.name")]);

    private static WorkQueueOutcome Expired(string parentName)
        => WorkQueueOutcome.Invalid(
            [WorkMessage.Error(
                "workable.child_execution.scope_expired",
                $"The child execution scope for '{parentName}' is no longer active.",
                "child.execution")]);

    private static WorkInput? ToWorkInput<TInput>(TInput input)
        => input switch
        {
            null => null,
            WorkInput workInput => workInput,
            _ => WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
        };
}
