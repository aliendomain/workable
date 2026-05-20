using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class WorkQueueService(
    WorkSystemCatalog catalog,
    WorkerOperations workers,
    IDotNetWorkOriginProvider dotNetOriginProvider,
    WorkSystemQueueDiagnosticsTracker queueDiagnostics) :
    IWorkQueueService,
    IRequestContextWorkQueueService
{
    public Task<IWorkerHandle> Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => ((IRequestContextWorkQueueService)this).Enqueue(
            definitionId,
            input,
            options,
            new WorkRequestContext(
                dotNetOriginProvider.CreateOrigin($"Queue work definition '{definitionId.Value:D}' through .NET.")),
            cancellationToken);

    Task<IWorkerHandle> IRequestContextWorkQueueService.Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
        => !catalog.TryGetWork(definitionId, out var registeredWork)
            ? Task.FromResult<IWorkerHandle>(Reject(WorkQueueOutcome.NotFound(definitionId.ToString())))
            : workers.CreateWorker(registeredWork, input, options, requestContext, cancellationToken);

    public Task<IWorkerHandle> Enqueue<TInput>(
        WorkDefinitionId definitionId,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Enqueue(definitionId, ToWorkInput(input), options, cancellationToken);

    public Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return ((IRequestContextWorkQueueService)this).Enqueue(
            name,
            input,
            options,
            new WorkRequestContext(
                dotNetOriginProvider.CreateOrigin($"Queue work '{name}' through .NET.")),
            cancellationToken);
    }

    Task<IWorkerHandle> IRequestContextWorkQueueService.Enqueue(
        string name,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return !catalog.TryGetWork(name, out var registeredWork)
            ? Task.FromResult<IWorkerHandle>(Reject(WorkQueueOutcome.NotFound(name)))
            : workers.CreateWorker(registeredWork, input, options, requestContext, cancellationToken);
    }

    public Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.Enqueue(name, ToWorkInput(input), options, cancellationToken);

    private static WorkInput? ToWorkInput<TInput>(TInput input)
        => input switch
        {
            null => null,
            WorkInput workInput => workInput,
            _ => WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
        };

    private WorkerHandle Reject(WorkQueueOutcome outcome)
    {
        queueDiagnostics.RecordRejected(outcome);
        return WorkerHandle.Rejected(outcome);
    }
}
