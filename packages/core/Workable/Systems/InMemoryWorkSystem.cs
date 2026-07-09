using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workable;
internal sealed class InMemoryWorkSystem :
    IWorkSystem,
    IWorkSystemBuiltInHttpSurfaceAccess,
    IWorkSystemReadModelClock,
    IWorkSystemWorkflowClock,
    IWorkSystemShutdownMetadata,
    IWorkSystemCapabilitySource
{
    private readonly IServiceProvider rootServices;
    private readonly ILogger<InMemoryWorkSystem>? logger;
    private readonly IWorkAuthorizationGroupProvider groupProvider;
    private readonly WorkSystemAuthorizationConfiguration authorization;
    private readonly IReadOnlyList<Func<IServiceProvider, IWorkDefinitionSource>> workDefinitionSourceFactories;
    private readonly IReadOnlyList<Func<IServiceProvider, IStartupWorkSource>> startupWorkSourceFactories;
    private readonly WorkSystemCatalog catalog;
    private readonly WorkflowCatalog workflows;
    private readonly WorkflowRuntime workflowRuntime;
    private readonly WorkflowPersistenceCoordinator workflowPersistence;
    private readonly WorkQueueService queue;
    private readonly WorkerOperations workers;
    private readonly WorkSystemReadModel readModel;
    private readonly WorkSystemReadModelQueryService query;
    private readonly WorkSystemDiagnostics diagnostics;
    private readonly WorkSystemSessionFactory sessions;
    private readonly InMemoryWorkMetricsSink metrics = new();
    private readonly WorkEventStream events = new();
    private readonly WorkChangeStream changes = new();
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
        WorkerOptions? implicitDefaultWorkerOptions,
        IReadOnlyList<WorkExceptionClassifier> globalExceptionClassifiers)
    {
        this.Id = registration.Id;
        this.Name = registration.Name;
        this.RequiresAuthorization = registration.RequiresAuthorization;
        this.authorization = registration.Authorization;
        this.rootServices = rootServices;
        this.logger = rootServices.GetService<ILogger<InMemoryWorkSystem>>();
        this.workDefinitionSourceFactories = workDefinitionSourceFactories;
        this.startupWorkSourceFactories = startupWorkSourceFactories;
        this.ShutdownGracePeriod = shutdownGracePeriod;
        var persistenceStore = rootServices.GetService<IWorkPersistenceStore>();
        var capabilities = new WorkSystemCapabilitiesBuilder
        {
            PersistentCoordinationAvailable = persistenceStore is not null,
        };
        foreach (var contributor in rootServices.GetServices<IWorkSystemCapabilityContributor>())
        {
            contributor.ConfigureCapabilities(capabilities);
        }

        this.Capabilities = capabilities.Build();
        var authorizationLogger = rootServices.GetService<ILoggerFactory>()?.CreateLogger("Workable.Authorization");
        this.catalog = new WorkSystemCatalog(
            work,
            this.Capabilities.PersistentCoordinationAvailable,
            implicitDefaultWorkerOptions,
            this.authorization,
            authorizationLogger,
            this.changes);
        this.workflows = new WorkflowCatalog(registration.Workflows);
        this.workflowPersistence = new WorkflowPersistenceCoordinator(
            persistenceStore,
            this.Name);
        this.readModel = new WorkSystemReadModel(this.catalog, () => this.State, this.Name, this.metrics, this.changes);
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
            this.Capabilities,
            () => this.State,
            this.diagnostics,
            this.catalog,
            this.workflows,
            this.queue,
            this.workers,
            this.query,
            this.events,
            this.changes,
            this.authorization,
            this.groupProvider);
        var workflowEvents = new WorkflowEventPublisher(this.Id, this.Name, this.events);
        this.workflowRuntime = new WorkflowRuntime(
            this.Name,
            this.RequiresAuthorization,
            this.workflows,
            workDefinitionName => this.catalog.TryGetWork(workDefinitionName, out var registeredWork) ? registeredWork : null,
            this.CreateSession,
            this.workers.CreateHandle,
            this.workers.GetAuthoritative,
            this.workflowPersistence,
            this.authorization,
            this.groupProvider,
            workflowEvents);
        this.workers.SetWorkflowChildFinalizationObserver(this.workflowRuntime.ObserveFinalWorkflowChild);
        this.workers.SetWorkflowChildRetentionGuard(this.workflowRuntime.ShouldKeepWorkflowChildWorker);
        this.workers.SetWorkflowChildPurgedObserver(this.workflowRuntime.ObservePurgedWorkflowChild);
        this.workers.SetCompletionObserver((worker, status) =>
        {
            if (!worker.Identifiers.Any(identifier => identifier.Type == "workflow-run"))
            {
                return;
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        if (status == WorkCompletionStatus.Completed)
                        {
                            await this.workflowRuntime.TryAutoResumeBlockedRunForCompletedWorker(worker.Id, CancellationToken.None);
                        }
                    }
                    catch (Exception exception) when (IsNonCriticalException(exception))
                    {
                        this.logger?.LogWarning(
                            exception,
                            "Workflow auto-resume processing failed for worker {WorkerId} in work system {WorkSystem}.",
                            worker.Id.Value,
                            this.Name ?? this.Id.ToString());
                    }
                },
                CancellationToken.None);
        });
    }

    public WorkSystemId Id { get; }

    public string? Name { get; }

    public bool RequiresAuthorization { get; }

    public WorkSystemState State { get; private set; } = WorkSystemState.Created;

    public TimeSpan ShutdownGracePeriod { get; }

    public WorkSystemCapabilities Capabilities { get; }

    internal WorkflowCatalog Workflows => this.workflows;

    internal WorkflowRuntime WorkflowRuntime => this.workflowRuntime;

    internal WorkerOperations WorkerOperations => this.workers;
    internal WorkChangeStream ChangeStream => this.changes;

    long IWorkSystemReadModelClock.AppliedSequence => this.readModel.AppliedSequence;

    long IWorkSystemWorkflowClock.WorkflowSequence => this.workflowRuntime.Version;

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

    public IWorkChangeStream Changes
    {
        get
        {
            this.ThrowIfAuthorizationRequiredForDirectAccess();
            return this.changes;
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

    bool IWorkSystemBuiltInHttpSurfaceAccess.IsBuiltInHttpSurfaceAllowed(WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestContext);

        if (!this.RequiresAuthorization)
        {
            return true;
        }

        var resolvedAuthorization = this.ResolveAuthorization(requestContext);
        return resolvedAuthorization.CanUseBuiltInHttpApiSurface();
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
        var lifecycleStarted = false;
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
            await this.workflowPersistence.Initialize(this.workflows.Definitions, cancellationToken);
            this.workflowRuntime.StartExecutionLifetime();
            await this.workers.StartDispatching(cancellationToken);
            dispatchingStarted = true;
            this.State = WorkSystemState.Started;
            lifecycleStarted = true;
            await this.workflowRuntime.RecoverDurableRuns(cancellationToken);
            await this.NotifyStarted(cancellationToken);
            await this.QueueAutomaticallyStartedWork(cancellationToken);
            await this.QueueStartupWork(cancellationToken);
        }
        catch (Exception exception)
        {
            var cleanupExceptions = new List<Exception>();
            TryCleanup(() => this.workflowRuntime.CancelExecutionLifetime(), cleanupExceptions);
            if (dispatchingStarted)
            {
                await TryCleanupAsync(
                    () => this.workers.StopDispatching(CancellationToken.None),
                    cleanupExceptions);
            }

            await TryCleanupAsync(
                () => this.workflowRuntime.WaitForExecutions(CancellationToken.None),
                cleanupExceptions);
            await TryCleanupAsync(
                () => this.workflowRuntime.StopBackgroundTasks(CancellationToken.None),
                cleanupExceptions);
            TryCleanup(() => this.workflowRuntime.ClearRuns(), cleanupExceptions);

            if (lifecycleStarted)
            {
                await TryCleanupAsync(
                    () => this.NotifyStopped(),
                    cleanupExceptions);
            }

            this.metrics.Clear();
            this.State = WorkSystemState.Stopped;
            if (cleanupExceptions.Count > 0)
            {
                throw new AggregateException(
                    "Workable failed to start and one or more cleanup operations also failed.",
                    [exception, .. cleanupExceptions]);
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
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
            var cleanupExceptions = new List<Exception>();
            await TryCleanupAsync(() => this.NotifyStopping(requestContext.Origin), cleanupExceptions);
            TryCleanup(() => this.workflowRuntime.CancelExecutionLifetime(), cleanupExceptions);

            WorkSystemStopResult? result = null;
            await TryCleanupAsync(
                async () => result = await this.workers.StopDispatching(requestContext, cancellationToken),
                cleanupExceptions);
            await TryCleanupAsync(
                () => this.workflowRuntime.WaitForExecutions(cancellationToken),
                cleanupExceptions);
            await TryCleanupAsync(
                () => this.workflowRuntime.StopBackgroundTasks(cancellationToken),
                cleanupExceptions);
            TryCleanup(() => this.workflowRuntime.ClearRuns(), cleanupExceptions);
            await TryCleanupAsync(() => this.NotifyStopped(), cleanupExceptions);
            this.metrics.Clear();
            this.State = WorkSystemState.Stopped;
            if (cleanupExceptions.Count > 0)
            {
                throw new AggregateException(
                    "Workable failed while stopping and one or more cleanup operations also failed.",
                    cleanupExceptions);
            }

            return result ?? new WorkSystemStopResult([]);
        }
        finally
        {
            this.lifecycleLock.Release();
        }
    }

    private static void TryCleanup(Action cleanup, List<Exception> exceptions)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception) when (ShouldCaptureCleanupException(exception))
        {
            exceptions.Add(exception);
        }
    }

    private static async Task TryCleanupAsync(Func<Task> cleanup, List<Exception> exceptions)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception) when (ShouldCaptureCleanupException(exception))
        {
            exceptions.Add(exception);
        }
    }

    private static bool ShouldCaptureCleanupException(Exception exception)
        => exception is not (
            OperationCanceledException or
            OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            ThreadAbortException);

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

    private async Task NotifyStarted(CancellationToken cancellationToken)
    {
        foreach (var observer in this.rootServices.GetServices<IWorkSystemLifecycleObserver>())
        {
            await observer.SystemStarted(this, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopCore(
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        this.workers.Dispose();
        this.lifecycleLock.Dispose();
        await this.readModel.DisposeAsync();
        await this.events.DisposeAsync();
        await this.changes.DisposeAsync();
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

                var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
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
            var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
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

    private async Task NotifyStopped()
    {
        foreach (var observer in this.rootServices.GetServices<IWorkSystemLifecycleObserver>())
        {
            try
            {
                await observer.SystemStopped(this, CancellationToken.None);
            }
            catch (Exception exception) when (IsNonCriticalException(exception))
            {
                this.logger?.LogWarning(
                    exception,
                    "Lifecycle observer {ObserverType} threw during SystemStopped for work system {WorkSystem}.",
                    observer.GetType().FullName ?? observer.GetType().Name,
                    this.Name ?? this.Id.ToString());
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

    private static bool IsNonCriticalException(Exception exception)
        => exception is not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException and
            not AppDomainUnloadedException and
            not BadImageFormatException and
            not CannotUnloadAppDomainException and
            not InvalidProgramException and
            not global::System.Threading.ThreadAbortException;
}
