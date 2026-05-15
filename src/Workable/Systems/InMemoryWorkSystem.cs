using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class InMemoryWorkSystem : IWorkSystem, IOriginAwareWorkSystem, IWorkSystemShutdownMetadata
{
    private readonly IServiceProvider rootServices;
    private readonly IReadOnlyList<Func<IServiceProvider, IWorkDefinitionSource>> workDefinitionSourceFactories;
    private readonly IReadOnlyList<Func<IServiceProvider, IStartupWorkSource>> startupWorkSourceFactories;
    private readonly WorkSystemCatalog catalog;
    private readonly WorkQueue queue;
    private readonly WorkerOperations workers;
    private readonly InMemoryWorkMetricsSink metrics = new();
    private readonly WorkEventStream events = new();
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private bool runtimeWorkDefined;

    public InMemoryWorkSystem(
        WorkSystemRegistration registration,
        IReadOnlyList<RegisteredWork> work,
        IReadOnlyList<Func<IServiceProvider, IWorkDefinitionSource>> workDefinitionSourceFactories,
        IReadOnlyList<Func<IServiceProvider, IStartupWorkSource>> startupWorkSourceFactories,
        IServiceProvider rootServices,
        TimeSpan shutdownGracePeriod,
        IReadOnlyList<WorkExceptionClassifier> globalExceptionClassifiers)
    {
        this.Id = registration.Id;
        this.Name = registration.Name;
        this.rootServices = rootServices;
        this.workDefinitionSourceFactories = workDefinitionSourceFactories;
        this.startupWorkSourceFactories = startupWorkSourceFactories;
        this.catalog = new WorkSystemCatalog(work);
        this.ShutdownGracePeriod = shutdownGracePeriod;
        var dotNetOriginProvider = registration.DotNetOriginProviderFactory?.Invoke(rootServices)
            ?? rootServices.GetService<IDotNetWorkOriginProvider>()
            ?? new DefaultDotNetWorkOriginProvider();
        this.workers = new WorkerOperations(this.catalog, () => this.State, this.Id, this.Name, rootServices, this.events, dotNetOriginProvider, registration.ExceptionClassifiers, globalExceptionClassifiers, this.ShutdownGracePeriod, this.metrics);
        this.queue = new WorkQueue(this.catalog, this.workers, dotNetOriginProvider);
    }

    public WorkSystemId Id { get; }

    public string? Name { get; }

    public WorkSystemState State { get; private set; } = WorkSystemState.Created;

    public TimeSpan ShutdownGracePeriod { get; }

    public IWorkCatalog Catalog => this.catalog;

    public IWorkQueue Queue => this.queue;

    public IWorkerOperations Workers => this.workers;

    public IWorkQuery Query => this.workers;

    public IWorkEventStream Events => this.events;

    Task<IWorkerHandle> IOriginAwareWorkSystem.Enqueue(
        string name,
        WorkInput? input,
        WorkerOptions? options,
        WorkOrigin origin,
        CancellationToken cancellationToken)
        => this.queue.Enqueue(name, input, options, origin, cancellationToken);

    Task<IWorkerHandle> IOriginAwareWorkSystem.Enqueue(
        WorkDefinitionId definitionId,
        WorkInput? input,
        WorkerOptions? options,
        WorkOrigin origin,
        CancellationToken cancellationToken)
        => this.queue.Enqueue(definitionId, input, options, origin, cancellationToken);

    Task<WorkActionOutcome> IOriginAwareWorkSystem.Execute(
        WorkerVersion worker,
        WorkAction action,
        WorkOrigin origin,
        CancellationToken cancellationToken)
        => this.workers.Execute(worker, action, origin, cancellationToken);

    Task<WorkerBulkActionOutcome> IOriginAwareWorkSystem.ExecuteAll(
        WorkAction action,
        WorkerBulkActionFilter? filter,
        WorkOrigin origin,
        CancellationToken cancellationToken)
        => this.workers.ExecuteAll(action, filter, origin, cancellationToken);

    Task<WorkActionOutcome> IOriginAwareWorkSystem.Reconfigure(
        WorkerVersion worker,
        WorkerReconfiguration changes,
        WorkOrigin origin,
        CancellationToken cancellationToken)
        => this.workers.Reconfigure(worker, changes, origin, cancellationToken);

    Task<WorkSystemStopResult> IOriginAwareWorkSystem.Stop(
        WorkOrigin origin,
        CancellationToken cancellationToken)
        => this.Stop(origin, cancellationToken);

    public async Task Start(CancellationToken cancellationToken = default)
    {
        await this.lifecycleLock.WaitAsync(cancellationToken);
        var dispatchingStarted = false;
        try
        {
            if (this.State == WorkSystemState.Started)
            {
                return;
            }

            this.State = WorkSystemState.Starting;
            if (!this.runtimeWorkDefined)
            {
                await this.DefineRuntimeWork(cancellationToken);
                this.runtimeWorkDefined = true;
            }

            this.catalog.Freeze();
            this.workers.StartDispatching();
            dispatchingStarted = true;
            this.State = WorkSystemState.Started;
            await this.QueueAutomaticallyStartedWork(cancellationToken);
            await this.QueueStartupWork(cancellationToken);
        }
        catch
        {
            if (dispatchingStarted)
            {
                await this.workers.StopDispatching(CancellationToken.None);
            }

            this.State = WorkSystemState.Stopped;
            throw;
        }
        finally
        {
            this.lifecycleLock.Release();
        }
    }

    public Task<WorkSystemStopResult> Stop(CancellationToken cancellationToken = default)
        => this.Stop(
            WorkOrigin.Create(WorkInvocationChannel.DotNet, description: "Stop Workable system through .NET."),
            cancellationToken);

    private async Task<WorkSystemStopResult> Stop(
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);

        await this.lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (this.State == WorkSystemState.Stopped)
            {
                return new WorkSystemStopResult([]);
            }

            this.State = WorkSystemState.Stopping;
            var result = await this.workers.StopDispatching(origin, CancellationToken.None);
            this.State = WorkSystemState.Stopped;
            return result;
        }
        finally
        {
            this.lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.Stop();
        this.workers.Dispose();
        this.lifecycleLock.Dispose();
        await this.events.DisposeAsync();
    }

    private async Task DefineRuntimeWork(CancellationToken cancellationToken)
    {
        if (this.workDefinitionSourceFactories.Count == 0)
        {
            return;
        }

        await using var scope = this.rootServices.CreateAsyncScope();
        var builder = new RuntimeWorkDefinitionBuilder(this.catalog);
        foreach (var sourceFactory in this.workDefinitionSourceFactories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sourceFactory(scope.ServiceProvider);
            await source.DefineWork(builder, cancellationToken);
        }
    }

    private async Task QueueStartupWork(CancellationToken cancellationToken)
    {
        if (this.startupWorkSourceFactories.Count == 0)
        {
            return;
        }

        await using var scope = this.rootServices.CreateAsyncScope();
        foreach (var sourceFactory in this.startupWorkSourceFactories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sourceFactory(scope.ServiceProvider);
            var requests = await source.CreateStartupWork(cancellationToken);
            foreach (var request in requests)
            {
                var registeredWork = this.GetStartupRegisteredWork(request);
                var configuration = registeredWork.Definition.Configuration
                    .Merge(registeredWork.Definition.DefaultOptions.Configuration)
                    .Merge(request.Options?.Configuration);
                if (configuration.Start.Policy == WorkStartPolicy.StartAndReturnAfterCompleted)
                {
                    throw new InvalidOperationException(
                        $"Startup work '{registeredWork.Definition.Name}' cannot use '{nameof(WorkStartPolicy.StartAndReturnAfterCompleted)}'. Startup work is queued during system start and cannot wait for worker completion.");
                }

                var origin = WorkOrigin.Create(
                    WorkInvocationChannel.DotNet,
                    description: $"Queue startup work from '{source.GetType().FullName}'.");
                var handle = request.DefinitionId is { } definitionId
                    ? await this.queue.Enqueue(definitionId, request.Input, request.Options, origin, cancellationToken)
                    : await this.queue.Enqueue(request.Name ?? throw new InvalidOperationException("Startup work requests must provide a work definition id or name."), request.Input, request.Options, origin, cancellationToken);

                if (!handle.QueueOutcome.IsAccepted)
                {
                    var messages = string.Join("; ", handle.QueueOutcome.Messages.Select(message => message.Text));
                    throw new InvalidOperationException($"Startup work from '{source.GetType().FullName}' could not be queued. {messages}");
                }
            }
        }
    }

    private async Task QueueAutomaticallyStartedWork(CancellationToken cancellationToken)
    {
        var automaticallyStarted = this.catalog.RegisteredWork
            .Where(registeredWork => registeredWork.AutomaticStarts.Count > 0)
            .ToList();
        if (automaticallyStarted.Count == 0)
        {
            return;
        }

        await using var scope = this.rootServices.CreateAsyncScope();
        foreach (var registeredWork in automaticallyStarted)
        {
            foreach (var automaticStart in registeredWork.AutomaticStarts)
            {
                await this.QueueAutomaticStartInstances(
                    registeredWork,
                    automaticStart,
                    scope.ServiceProvider,
                    cancellationToken);
            }
        }
    }

    private async Task QueueAutomaticStartInstances(
        RegisteredWork registeredWork,
        WorkAutomaticStartRegistration automaticStart,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var configuration = registeredWork.Definition.Configuration
            .Merge(registeredWork.Definition.DefaultOptions.Configuration);
        if (configuration.Start.Policy == WorkStartPolicy.StartAndReturnAfterCompleted)
        {
            throw new InvalidOperationException(
                $"Automatically started work '{registeredWork.Definition.Name}' cannot use '{nameof(WorkStartPolicy.StartAndReturnAfterCompleted)}'. Automatically started work is queued during system start and cannot wait for worker completion.");
        }

        for (var i = 0; i < automaticStart.InstanceCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = automaticStart.InputFactory(services);
            var origin = WorkOrigin.Create(
                WorkInvocationChannel.DotNet,
                description: $"Queue automatically started work '{registeredWork.Definition.Name}'.");
            var handle = await this.queue.Enqueue(
                registeredWork.Definition.Id,
                input,
                null,
                origin,
                cancellationToken);

            if (!handle.QueueOutcome.IsAccepted)
            {
                var messages = string.Join("; ", handle.QueueOutcome.Messages.Select(message => message.Text));
                throw new InvalidOperationException($"Automatically started work '{registeredWork.Definition.Name}' could not be queued. {messages}");
            }
        }
    }

    private RegisteredWork GetStartupRegisteredWork(StartupWorkRequest request)
    {
        if (request.DefinitionId is { } definitionId)
        {
            return this.catalog.TryGetWork(definitionId, out var registeredWork)
                ? registeredWork
                : throw new InvalidOperationException($"Startup work definition '{definitionId.Value:D}' was not found.");
        }

        var name = request.Name ?? throw new InvalidOperationException("Startup work requests must provide a work definition id or name.");
        return this.catalog.TryGetWork(name, out var namedWork)
            ? namedWork
            : throw new InvalidOperationException($"Startup work definition '{name}' was not found.");
    }
}
