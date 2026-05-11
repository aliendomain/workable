using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal sealed class WorkerExecutionInvoker(
    WorkSystemId workSystemId,
    string? workSystemName,
    IServiceProvider rootServices,
    WorkerEventPublisher workerEvents,
    Action<WorkerRecord, WorkIdentifier> identifierDiscovered,
    WorkInitializationExecutor initialization)
{
    public async Task<WorkExecutionResult> Execute(WorkerRecord worker, CancellationToken cancellationToken)
    {
        var activeProfile = worker.Options.ProfilingEnabled
            ? new WorkProfile($"Worker {worker.Id.Value} {worker.Work.Definition.Name}")
            : null;
        using var profileContext = WorkProfilerContext.Begin(activeProfile);
        using var logCapture = WorkableLogCaptureContext.Begin(worker, workerEvents);
        try
        {
            var initializationResult = await initialization.Initialize(worker, this.CreateContext, cancellationToken);
            if (initializationResult.HasErrors)
            {
                return initializationResult;
            }

            await using var scope = rootServices.CreateAsyncScope();
            var context = this.CreateContext(worker, scope.ServiceProvider);
            var executor = worker.Work.ExecutorFactory(scope.ServiceProvider);
            using var executionScope = activeProfile?.CreateMethodScope(
                executor.GetType(),
                nameof(IWorkExecutor.Execute),
                worker.Input);
            var result = await executor.Execute(context, worker.Input, cancellationToken);
            executionScope?.SetResult(new
            {
                result.HasErrors,
                MessageCount = result.Messages.Count,
            });
            return result;
        }
        finally
        {
            if (activeProfile is not null)
            {
                worker.RecordProfile(activeProfile.ToSnapshot());
            }
        }
    }

    private WorkExecutionContext CreateContext(WorkerRecord worker, IServiceProvider services)
    {
        var profiler = services.GetService<IWorkProfiler>() ?? NoOpWorkProfiler.Instance;
        return new WorkExecutionContext(
            workSystemId,
            workSystemName,
            worker.Id,
            worker.Work.Definition,
            worker.Origin,
            worker.Options,
            worker.Configuration,
            profiler,
            services,
            identifier =>
            {
                var added = worker.AddIdentifier(identifier);
                if (added)
                {
                    identifierDiscovered(worker, identifier);
                }

                return added;
            });
    }
}
