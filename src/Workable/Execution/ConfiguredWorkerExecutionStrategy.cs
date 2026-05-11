namespace Workable;

internal sealed class ConfiguredWorkerExecutionStrategy(
    IWorkerExecutionStrategy runOnce,
    IWorkerExecutionStrategy transientRetry,
    IWorkerExecutionStrategy recurring) : IWorkerExecutionStrategy
{
    public Task<WorkCompletion> Execute(WorkerRecord worker, CancellationToken cancellationToken)
        => worker.Configuration.Recurrence.IsEnabled
            ? recurring.Execute(worker, cancellationToken)
            : worker.Configuration.TransientRetry.Count > 0
            ? transientRetry.Execute(worker, cancellationToken)
            : runOnce.Execute(worker, cancellationToken);
}
