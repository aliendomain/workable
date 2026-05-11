using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkerHandle
{
    WorkQueueOutcome QueueOutcome { get; }

    WorkerId? WorkerId { get; }

    Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default);

    Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default);
}
