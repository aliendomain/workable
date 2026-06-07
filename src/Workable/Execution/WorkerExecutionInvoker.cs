using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal sealed class WorkerExecutionInvoker(
    WorkSystemId workSystemId,
    string? workSystemName,
    IServiceProvider rootServices,
    IWorkerPersistenceCoordinator persistence,
    WorkerEventPublisher workerEvents,
    Action<WorkerRecord, WorkIdentifier> identifierDiscovered,
    WorkInitializationExecutor initialization)
{
    public async Task<WorkerExecutionInvocationResult> Execute(WorkerRecord worker, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeProfile = worker.Options.ProfilingEnabled
            ? new WorkProfile($"Worker {worker.Id.Value} {worker.Work.Definition.Name}")
            : null;
        using var profileContext = WorkProfilerContext.Begin(activeProfile);
        using var logCapture = WorkableLogCaptureContext.Begin(worker, workerEvents);
        try
        {
            IWorkExecutionContext CreateDurableContext(WorkerRecord contextWorker, IServiceProvider services)
                => this.CreateContext(contextWorker, services);

            var initializationResult = await initialization.Initialize(worker, CreateDurableContext, cancellationToken);
            if (initializationResult.HasErrors)
            {
                return new WorkerExecutionInvocationResult(initializationResult, RequestedFailureIsTransient: false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            await using var scope = rootServices.CreateAsyncScope();
            var context = this.CreateContext(worker, scope.ServiceProvider);
            var executor = worker.Work.ExecutorFactory(scope.ServiceProvider);
            using var executionScope = activeProfile?.CreateMethodScope(
                executor.GetType(),
                nameof(IWorkExecutor.Execute),
                worker.Input);
            var result = ApplyRequestedFailure(
                await executor.Execute(context, worker.Input, cancellationToken),
                context);
            executionScope?.SetResult(new
            {
                result.Result.HasErrors,
                MessageCount = result.Result.Messages.Count,
            });

            if (worker.Configuration.Coordination.Durability.CompleteDurably &&
                !result.Result.HasErrors &&
                worker.State == WorkerState.Running &&
                !context.IsDurableCompletionRecorded)
            {
                throw new InvalidOperationException(
                    "Durable completion is enabled for this work. Successful executor code must call IWorkExecutionContext.CompleteDurably with the developer-owned transaction before committing it.");
            }

            return result;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (activeProfile is not null)
            {
                worker.RecordProfile(activeProfile.ToSnapshot());
            }
        }
    }

    private WorkExecutionContext CreateContext(
        WorkerRecord worker,
        IServiceProvider services)
    {
        var profiler = services.GetService<IWorkProfiler>() ?? NoOpWorkProfiler.Instance;
        return new WorkExecutionContext(
            workSystemId,
            workSystemName,
            worker.Id,
            worker.Work.Definition,
            worker.RequestContext,
            worker.Options,
            worker.Configuration,
            () => worker.InterruptionReason,
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
            },
            (transaction, durableCompletionCancellation) =>
                persistence.CompleteDurably(worker, transaction, durableCompletionCancellation));
    }

    private static WorkerExecutionInvocationResult ApplyRequestedFailure(
        WorkExecutionResult result,
        WorkExecutionContext context)
    {
        var failure = context.RequestedFailure;
        if (failure is null)
        {
            return new WorkerExecutionInvocationResult(result, RequestedFailureIsTransient: false);
        }

        var messages = result.Messages.Contains(failure)
            ? result.Messages
            : (IReadOnlyList<WorkMessage>)[failure, .. result.Messages];
        return new WorkerExecutionInvocationResult(
            WorkExecutionResult.Failure(messages, result.Output),
            context.IsRequestedFailureTransient);
    }
}

internal sealed record WorkerExecutionInvocationResult(
    WorkExecutionResult Result,
    bool RequestedFailureIsTransient);
