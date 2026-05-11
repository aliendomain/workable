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

    public WorkerHandle(WorkQueueOutcome queueOutcome, WorkerRecord worker)
    {
        this.QueueOutcome = queueOutcome;
        this.worker = worker;
    }

    private WorkerHandle(WorkQueueOutcome queueOutcome)
    {
        this.QueueOutcome = queueOutcome;
    }

    public WorkQueueOutcome QueueOutcome { get; }

    public WorkerId? WorkerId => this.QueueOutcome.WorkerId;

    public static WorkerHandle Rejected(WorkQueueOutcome outcome)
        => new(outcome);

    public async Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
    {
        if (this.worker is null)
        {
            var status = this.QueueOutcome.Status == WorkQueueStatus.NotFound
                ? WorkCompletionStatus.NotFound
                : WorkCompletionStatus.Invalid;

            return new WorkCompletion(status, null, null, this.QueueOutcome.Messages);
        }

        return await this.worker.WaitForCompletion(cancellationToken);
    }

    public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
        => (await this.WaitForCompletion(cancellationToken)).ToTyped<TOutput>();
}
