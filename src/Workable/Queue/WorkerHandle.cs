using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class WorkerHandle : IWorkerHandle
{
    private readonly WorkerRecord? worker;
    private readonly Func<WorkerId, CancellationToken, Task<WorkerRecord?>>? resolveWorker;

    public WorkerHandle(WorkQueueOutcome queueOutcome, WorkerRecord worker)
    {
        this.QueueOutcome = queueOutcome;
        this.worker = worker;
    }

    private WorkerHandle(WorkQueueOutcome queueOutcome)
    {
        this.QueueOutcome = queueOutcome;
    }

    private WorkerHandle(
        WorkQueueOutcome queueOutcome,
        Func<WorkerId, CancellationToken, Task<WorkerRecord?>> resolveWorker)
    {
        this.QueueOutcome = queueOutcome;
        this.resolveWorker = resolveWorker;
    }

    public WorkQueueOutcome QueueOutcome { get; }

    public WorkerId? WorkerId => this.QueueOutcome.WorkerId;

    public static WorkerHandle Rejected(WorkQueueOutcome outcome)
        => new(outcome);

    public static WorkerHandle AcceptedWhenAvailable(
        WorkQueueOutcome outcome,
        Func<WorkerId, CancellationToken, Task<WorkerRecord?>> resolveWorker)
        => new(outcome, resolveWorker);

    public async Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
    {
        var resolvedWorker = this.worker;
        if (resolvedWorker is null && this.resolveWorker is not null && this.WorkerId is { } workerId)
        {
            resolvedWorker = await this.resolveWorker(workerId, cancellationToken);
        }

        if (resolvedWorker is null)
        {
            var status = this.QueueOutcome.Status == WorkQueueStatus.NotFound
                ? WorkCompletionStatus.NotFound
                : WorkCompletionStatus.Invalid;

            return new WorkCompletion(status, null, null, this.QueueOutcome.Messages);
        }

        return await resolvedWorker.WaitForCompletion(cancellationToken);
    }

    public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
        => (await this.WaitForCompletion(cancellationToken)).ToTyped<TOutput>();
}
