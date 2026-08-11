using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal sealed class WorkerExecutionInvoker(
    WorkSystemId workSystemId,
    string? workSystemName,
    IServiceProvider rootServices,
    IWorkerPersistenceCoordinator persistence,
    WorkerEventPublisher workerEvents,
    Action<WorkerRecord, WorkIdentifier> identifierDiscovered,
    WorkInitializationExecutor initialization,
    WorkIterationStatusStream? iterationStatuses = null,
    WorkSystemProfilingConfiguration? profilingConfiguration = null,
    WorkExecutionDiagnosticsCoordinator? executionDiagnostics = null,
    Func<WorkerRecord, IChildWorkQueueService>? childQueueFactory = null)
{
    public async Task<WorkerExecutionInvocationResult> Execute(WorkerRecord worker, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profileCaptureMode = executionDiagnostics?.ResolveProfileCaptureMode(worker) ??
            (worker.Options.ProfilingEnabled ? worker.Options.ProfilingCaptureMode : null);
        var executionOptions = profileCaptureMode is { } effectiveProfileCaptureMode &&
            (!worker.Options.ProfilingEnabled || worker.Options.ProfilingCaptureMode != effectiveProfileCaptureMode)
                ? worker.Options with
                {
                    ProfilingEnabled = true,
                    ProfilingCaptureMode = effectiveProfileCaptureMode,
                }
                : worker.Options;
        var activeProfile = profileCaptureMode is { } captureMode
            ? new WorkProfile(
                $"Worker {worker.Id.Value} {worker.Work.Definition.Name}",
                (profilingConfiguration ?? WorkSystemProfilingConfiguration.Default)
                    .MaximumAutomaticInstrumentationNodes,
                captureMode)
            : null;
        using var profileContext = WorkProfilerContext.Begin(workSystemId, activeProfile);
        using var logCapture = WorkableLogCaptureContext.Begin(worker, workerEvents, executionDiagnostics);
        try
        {
            IWorkExecutionContext CreateDurableContext(WorkerRecord contextWorker, IServiceProvider services)
                => this.CreateContext(contextWorker, services, executionOptions);

            var initializationResult = await initialization.Initialize(worker, CreateDurableContext, cancellationToken);
            if (initializationResult.HasErrors)
            {
                return new WorkerExecutionInvocationResult(initializationResult, RequestedFailureIsTransient: false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            await using var scope = rootServices.CreateAsyncScope();
            var context = this.CreateContext(worker, scope.ServiceProvider, executionOptions);
            using var childQueueScope = ChildWorkQueueContext.Begin(context.ChildQueue);
            try
            {
                var executor = worker.Work.ExecutorFactory(scope.ServiceProvider);
                using var executionScope = activeProfile?.CreateMethodScope(
                    executor.GetType(),
                    nameof(IWorkExecutor.Execute),
                    worker.Input);
                var invocation = ApplyRequestedFailure(
                    await executor.Execute(context, worker.Input, cancellationToken),
                    context);
                executionScope?.SetResult(new
                {
                    invocation.Result.HasErrors,
                    MessageCount = invocation.Result.Messages.Count,
                });

                if (worker.Configuration.Coordination.Durability.CompleteDurably &&
                    !invocation.Result.HasErrors &&
                    worker.State == WorkerState.Running &&
                    !context.IsDurableCompletionRecorded)
                {
                    throw new InvalidOperationException(
                        "Durable completion is enabled for this work. Successful executor code must call IWorkExecutionContext.CompleteDurably with the developer-owned transaction before committing it.");
                }

                return invocation;
            }
            finally
            {
                context.RevokeChildExecution();
            }
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (activeProfile is not null)
            {
                if (executionDiagnostics?.TryCaptureProfile(worker, activeProfile) != true)
                {
                    worker.RecordProfile(activeProfile.ToSnapshot());
                }
            }
        }
    }

    private WorkExecutionContext CreateContext(
        WorkerRecord worker,
        IServiceProvider services,
        WorkerOptions executionOptions)
    {
        var profiler = services.GetService<IWorkProfiler>() ?? NoOpWorkProfiler.Instance;
        return new WorkExecutionContext(
            workSystemId,
            workSystemName,
            worker.Id,
            worker.Work.Definition,
            worker.RequestContext,
            () => worker.CancellationRequestContext,
            executionOptions,
            worker.Configuration,
            () => worker.InterruptionReason,
            profiler,
            services,
            childQueueFactory?.Invoke(worker) ?? UnavailableChildWorkQueueService.Instance,
            iterationStatuses is null
                ? EmptyWorkIterationStatusPublisher.Instance
                : new WorkIterationStatusPublisher(
                    iterationStatuses,
                    worker.GetCurrentIterationReference(),
                    worker.Work.Definition.Name),
            identifier =>
            {
                var added = worker.AddIdentifier(identifier);
                if (added)
                {
                    identifierDiscovered(worker, identifier);
                }

                return added;
            },
            failedWorkerOverride => worker.SetFailedWorkerAutoCancelOverride(failedWorkerOverride),
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
