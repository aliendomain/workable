using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class InMemoryWorkSystem :
    IWorkSystem,
    IWorkSystemReadModelClock,
    IWorkSystemShutdownMetadata,
    IWorkSystemCoordinationCapabilities
{
    private readonly IServiceProvider rootServices;
    private readonly IWorkAuthorizationGroupProvider groupProvider;
    private readonly WorkSystemAuthorizationConfiguration authorization;
    private readonly IReadOnlyList<Func<IServiceProvider, IWorkDefinitionSource>> workDefinitionSourceFactories;
    private readonly IReadOnlyList<Func<IServiceProvider, IStartupWorkSource>> startupWorkSourceFactories;
    private readonly WorkSystemCatalog catalog;
    private readonly WorkQueueService queue;
    private readonly WorkerOperations workers;
    private readonly WorkSystemReadModel readModel;
    private readonly WorkSystemReadModelQueryService query;
    private readonly WorkSystemDiagnostics diagnostics;
    private readonly WorkSystemSessionFactory sessions;
    private readonly InMemoryWorkMetricsSink metrics = new();
    private readonly WorkEventStream events = new();
    private readonly WorkSystemQueueDiagnosticsTracker queueDiagnostics = new();
    private readonly WorkSystemIdempotencyDiagnosticsTracker idempotencyDiagnostics = new();
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
        this.RequiresAuthorization = registration.RequiresAuthorization;
        this.authorization = registration.Authorization;
        this.rootServices = rootServices;
        this.workDefinitionSourceFactories = workDefinitionSourceFactories;
        this.startupWorkSourceFactories = startupWorkSourceFactories;
        this.ShutdownGracePeriod = shutdownGracePeriod;
        var persistenceStore = rootServices.GetService<IWorkPersistenceStore>();
        this.PersistentCoordinationAvailable = persistenceStore is not null;
        this.catalog = new WorkSystemCatalog(work, this.PersistentCoordinationAvailable);
        this.readModel = new WorkSystemReadModel(this.catalog, () => this.State, this.Name, this.metrics);
        this.workers = new WorkerOperations(
            this.catalog,
            () => this.State,
            this.Id,
            this.Name,
            rootServices,
            this.events,
            this.readModel,
            registration.ExceptionClassifiers,
            globalExceptionClassifiers,
            this.ShutdownGracePeriod,
            registration.Retention,
            registration.Capacity,
            this.metrics,
            this.queueDiagnostics,
            this.idempotencyDiagnostics,
            persistenceStore);
        this.diagnostics = new WorkSystemDiagnostics(this.queueDiagnostics, this.readModel, this.workers);
        this.readModel.UseDetailReaders(this.workers.GetAuthoritative, this.workers.GetIterationAuthoritative);
        this.query = this.readModel.Query;
        this.queue = new WorkQueueService(this.catalog, this.workers, this.queueDiagnostics);
        this.groupProvider = rootServices.GetService<IWorkAuthorizationGroupProvider>() ?? EmptyWorkAuthorizationGroupProvider.Instance;
        this.sessions = new WorkSystemSessionFactory(
            this.Id,
            this.Name,
            () => this.State,
            this.diagnostics,
            this.catalog,
            this.queue,
            this.workers,
            this.query,
            this.events,
            this.authorization,
            this.groupProvider);
    }

    public WorkSystemId Id { get; }

    public string? Name { get; }

    public bool RequiresAuthorization { get; }

    public WorkSystemState State { get; private set; } = WorkSystemState.Created;

    public TimeSpan ShutdownGracePeriod { get; }

    public bool PersistentCoordinationAvailable { get; }

    long IWorkSystemReadModelClock.AppliedSequence => this.readModel.AppliedSequence;

    public IWorkCatalog Catalog
    {
        get
        {
            this.ThrowIfAuthorizationRequiredForDirectAccess();
            return this.catalog;
        }
    }

    public IWorkQueueService Queue
    {
        get
        {
            this.ThrowIfAuthorizationRequiredForDirectAccess();
            return this.queue;
        }
    }

    public IWorkerOperations Workers
    {
        get
        {
            this.ThrowIfAuthorizationRequiredForDirectAccess();
            return this.workers;
        }
    }

    public IWorkQueryService Query
    {
        get
        {
            this.ThrowIfAuthorizationRequiredForDirectAccess();
            return this.query;
        }
    }

    public IWorkEventStream Events
    {
        get
        {
            this.ThrowIfAuthorizationRequiredForDirectAccess();
            return this.events;
        }
    }

    public IWorkSystemDiagnostics Diagnostics
    {
        get
        {
            this.ThrowIfAuthorizationRequiredForDirectAccess();
            return this.diagnostics;
        }
    }

    public WorkSystemAccessSummary DescribeAccess(WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        var totalDefinitionCount = this.catalog.Definitions.Count;
        if (!this.RequiresAuthorization)
        {
            return new WorkSystemAccessSummary(
                IsSystemAdministrator: false,
                IsWorkAdministrator: false,
                CanViewDiagnostics: true,
                CanControlSystem: true,
                CanReadAllWork: true,
                CanOperateAllWork: true,
                TotalDefinitionCount: totalDefinitionCount,
                ReadableDefinitionCount: totalDefinitionCount,
                OperableDefinitionCount: totalDefinitionCount);
        }

        var groups = this.ResolveGroups(requestContext);
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(this.authorization, groups);
        var authorization = new WorkAuthorizationEvaluator(this.catalog, groups, false, systemAuthorization);
        var readableDefinitionCount = this.catalog.Definitions.Count(authorization.CanRead);
        var operableDefinitionCount = this.catalog.Definitions.Count(authorization.CanOperate);

        return new WorkSystemAccessSummary(
            systemAuthorization.IsSystemAdministrator(),
            systemAuthorization.IsWorkAdministrator(),
            systemAuthorization.CanViewDiagnostics(),
            systemAuthorization.CanControlSystem(),
            systemAuthorization.HasReadAllWorkAccess(),
            systemAuthorization.HasOperateAllWorkAccess(),
            totalDefinitionCount,
            readableDefinitionCount,
            operableDefinitionCount);
    }

    public IWorkSystemSession CreateSession(WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        return this.sessions.CreateSession(requestContext, this.RequiresAuthorization);
    }

    public Task Start(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        this.EnsureControlSystemAccess(requestContext);
        return this.StartCore(cancellationToken);
    }

    private void ThrowIfAuthorizationRequiredForDirectAccess()
    {
        if (this.RequiresAuthorization)
        {
            throw new WorkSystemAuthorizationRequiredException(this.Id, this.Name);
        }
    }

    private void EnsureControlSystemAccess(WorkRequestContext requestContext)
    {
        if (!this.RequiresAuthorization)
        {
            return;
        }

        if (!this.ResolveAuthorization(requestContext).CanControlSystem())
        {
            throw new WorkSystemAccessDeniedException(WorkSystemPermission.ControlSystem, this.Id, this.Name);
        }
    }

    private WorkSystemAuthorizationEvaluator ResolveAuthorization(WorkRequestContext requestContext)
        => new(this.authorization, this.ResolveGroups(requestContext));

    private IReadOnlySet<string> ResolveGroups(WorkRequestContext requestContext)
        => requestContext.Authorization?.Groups
            ?? this.groupProvider.GetGroups(requestContext.Actor, this.Name)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private async Task StartCore(CancellationToken cancellationToken = default)
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
            await this.workers.StartDispatching(cancellationToken);
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

    public Task<WorkSystemStopResult> Stop(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        this.EnsureControlSystemAccess(requestContext);
        return this.StopCore(requestContext, cancellationToken);
    }

    private async Task<WorkSystemStopResult> StopCore(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        await this.lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (this.State == WorkSystemState.Stopped)
            {
                return new WorkSystemStopResult([]);
            }

            this.State = WorkSystemState.Stopping;
            await this.NotifyStopping(requestContext.Origin);
            var result = await this.workers.StopDispatching(requestContext, cancellationToken);
            this.metrics.Clear();
            this.State = WorkSystemState.Stopped;
            return result;
        }
        finally
        {
            this.lifecycleLock.Release();
        }
    }

    private async Task NotifyStopping(WorkOrigin origin)
    {
        foreach (var observer in this.rootServices.GetServices<IWorkSystemLifecycleObserver>())
        {
            try
            {
                await observer.SystemStopping(this, origin, CancellationToken.None);
            }
            catch
            {
                // Lifecycle observers are best-effort and must not prevent shutdown.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopCore(
            WorkRequestContext.Create(WorkInvocationChannel.DotNet));
        this.workers.Dispose();
        this.lifecycleLock.Dispose();
        await this.readModel.DisposeAsync();
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
                var runtimePlan = request.Options is null
                    ? registeredWork.DefaultRuntimePlan
                    : RegisteredWorkRuntimePlan.Create(registeredWork.Definition, request.Options);
                if (runtimePlan.StartPolicy == WorkStartPolicy.StartAndReturnAfterCompleted)
                {
                    throw new InvalidOperationException(
                        $"Startup work '{registeredWork.Definition.Name}' cannot use '{nameof(WorkStartPolicy.StartAndReturnAfterCompleted)}'. Startup work is queued during system start and cannot wait for worker completion.");
                }

                var requestContext = WorkRequestContext.Create(WorkInvocationChannel.DotNet);
                var handle = request.DefinitionId is { } definitionId
                    ? await this.queue.Enqueue(definitionId, request.Input, request.Options, requestContext, cancellationToken)
                    : await this.queue.Enqueue(request.Name ?? throw new InvalidOperationException("Startup work requests must provide a work definition id or name."), request.Input, request.Options, requestContext, cancellationToken);

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
        if (registeredWork.DefaultRuntimePlan.StartPolicy == WorkStartPolicy.StartAndReturnAfterCompleted)
        {
            throw new InvalidOperationException(
                $"Automatically started work '{registeredWork.Definition.Name}' cannot use '{nameof(WorkStartPolicy.StartAndReturnAfterCompleted)}'. Automatically started work is queued during system start and cannot wait for worker completion.");
        }

        for (var i = 0; i < automaticStart.InstanceCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = automaticStart.InputFactory(services);
            var requestContext = WorkRequestContext.Create(WorkInvocationChannel.DotNet);
            var handle = await this.queue.Enqueue(
                registeredWork.Definition.Id,
                input,
                null,
                requestContext,
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
