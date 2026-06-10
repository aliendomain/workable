using Microsoft.AspNetCore.SignalR;

namespace Workable;

/// <summary>
/// SignalR hub that exposes Workable realtime event, named-view, and worker-overview subscriptions.
/// </summary>
/// <remarks>
/// This hub is observability-focused. Clients subscribe to updates here, then use direct .NET or HTTP APIs for
/// queueing, querying, and worker actions.
/// </remarks>
public sealed class WorkableRealtimeHub(
    IWorkSystemRegistry registry,
    IWorkAuthorizationGroupProvider groupProvider,
    IWorkRequestContextFactory requestContexts,
    WorkableViewQueryAdapter views,
    WorkableRealtimeEventSubscriptions eventSubscriptions,
    WorkableRealtimeViewSubscriptions viewSubscriptions,
    WorkableRealtimeWorkerOverviewSubscriptions workerOverviewSubscriptions) : Hub
{
    /// <summary>
    /// Starts or replaces a named view subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The caller-defined logical handle for this live view stream.</param>
    /// <param name="viewName">The built-in or custom view name to subscribe to.</param>
    /// <param name="criteria">Optional criteria used to scope the view components.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task WatchView(
        string subscriptionId,
        string viewName,
        WorkViewCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        WorkableRealtimeViewSubscription? subscription = null;
        try
        {
            var authorization = CreateAuthorization(system, systemName, out var session);
            subscription = await viewSubscriptions.WatchView(
                this.Context.ConnectionId,
                this.Groups,
                system,
                subscriptionId,
                viewName,
                views.NormalizeViewCriteria(viewName, criteria),
                authorization,
                this.Context.ConnectionAborted);

            await SendView(subscription, session, this.Clients.Caller);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while the initial view payload was being prepared.
        }
        catch
        {
            if (subscription is not null)
            {
                await viewSubscriptions.UnwatchView(
                    this.Context.ConnectionId,
                    this.Groups,
                    system,
                    subscription.SubscriptionId,
                    CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>
    /// Stops a named view subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The subscription id originally passed to <see cref="WatchView"/>.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public Task UnwatchView(string subscriptionId, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        EnsureCanAccessNamedSystem(
            system,
            systemName,
            CreateRequestContext());
        return viewSubscriptions.UnwatchView(
            this.Context.ConnectionId,
            this.Groups,
            system,
            subscriptionId,
            this.Context.ConnectionAborted);
    }

    /// <summary>
    /// Starts or replaces a worker-overview subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The caller-defined logical handle for this live worker-overview stream.</param>
    /// <param name="workerId">The worker identifier as a string-form GUID.</param>
    /// <param name="criteria">Optional realtime criteria that describe the visible worker detail screen state.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task WatchWorkerOverview(
        string subscriptionId,
        string workerId,
        WorkWorkerOverviewRealtimeCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        WorkableRealtimeWorkerOverviewSubscription? subscription = null;
        try
        {
            var authorization = CreateAuthorization(system, systemName, out var session);
            subscription = await workerOverviewSubscriptions.Watch(
                this.Context.ConnectionId,
                this.Groups,
                system,
                subscriptionId,
                ParseWorkerId(workerId),
                views.NormalizeWorkerOverviewRealtimeCriteria(criteria),
                authorization,
                this.Context.ConnectionAborted);
            await SendWorkerOverview(subscription, session, this.Clients.Caller);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while the worker overview stream was starting.
        }
        catch
        {
            if (subscription is not null)
            {
                await workerOverviewSubscriptions.Unwatch(
                    this.Context.ConnectionId,
                    this.Groups,
                    system,
                    subscription.SubscriptionId,
                    CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>
    /// Stops a worker-overview subscription for the current connection.
    /// </summary>
    /// <param name="subscriptionId">The subscription id originally passed to <see cref="WatchWorkerOverview"/>.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public Task UnwatchWorkerOverview(string subscriptionId, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        EnsureCanAccessNamedSystem(
            system,
            systemName,
            CreateRequestContext());
        return workerOverviewSubscriptions.Unwatch(
            this.Context.ConnectionId,
            this.Groups,
            system,
            subscriptionId,
            this.Context.ConnectionAborted);
    }

    /// <summary>
    /// Starts or replaces a raw event subscription for the current connection.
    /// </summary>
    /// <param name="criteria">Optional event-type, definition, and key filters.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task WatchEvents(
        WorkableRealtimeEventCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        try
        {
            var authorization = CreateAuthorization(system, systemName, out var session);
            await eventSubscriptions.WatchEvents(
                this.Context.ConnectionId,
                this.Groups,
                system,
                session.Catalog,
                criteria,
                authorization,
                this.Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while the event pump was starting.
        }
    }

    /// <summary>
    /// Stops a raw event subscription for the current connection.
    /// </summary>
    /// <param name="criteria">The same normalized filter shape originally used to watch the event stream.</param>
    /// <param name="systemName">Optional named system. When omitted, the default Workable system is used.</param>
    public async Task UnwatchEvents(
        WorkableRealtimeEventCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        var requestContext = CreateRequestContext();
        EnsureCanAccessNamedSystem(system, systemName, requestContext);
        var session = system.CreateSession(requestContext);
        await eventSubscriptions.UnwatchEvents(
            this.Context.ConnectionId,
            this.Groups,
            system,
            session.Catalog,
            criteria,
            this.Context.ConnectionAborted);
    }

    /// <summary>
    /// Removes all active subscriptions for the connection that is disconnecting.
    /// </summary>
    /// <param name="exception">The disconnect exception, if one caused the disconnect.</param>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await eventSubscriptions.RemoveConnection(this.Context.ConnectionId, this.Groups, this.Context.ConnectionAborted);
        await viewSubscriptions.RemoveConnection(this.Context.ConnectionId, this.Groups, this.Context.ConnectionAborted);
        await workerOverviewSubscriptions.RemoveConnection(this.Context.ConnectionId, this.Groups, this.Context.ConnectionAborted);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task SendView(
        WorkableRealtimeViewSubscription subscription,
        IWorkSystemSession session,
        IClientProxy client)
    {
        var result = await views.View(
            session,
            subscription.ViewName,
            subscription.Criteria,
            cancellationToken: this.Context.ConnectionAborted);
        await client.SendAsync(
            WorkableRealtimeClientMethods.ViewUpdated,
            new WorkableRealtimeViewEnvelope<WorkComponentQueryResult>(
                subscription.SubscriptionId,
                subscription.ViewName,
                result),
            this.Context.ConnectionAborted);
    }

    private async Task SendWorkerOverview(
        WorkableRealtimeWorkerOverviewSubscription subscription,
        IWorkSystemSession session,
        IClientProxy client)
    {
        var result = await views.WorkerOverviewRealtimeState(
            session,
            subscription.WorkerId,
            subscription.Criteria,
            cancellationToken: this.Context.ConnectionAborted);
        if (result is null)
        {
            return;
        }

        await client.SendAsync(
            WorkableRealtimeClientMethods.WorkerOverviewUpdated,
            new WorkableRealtimeViewEnvelope<WorkWorkerOverviewRealtimeUpdate>(
                subscription.SubscriptionId,
                "worker-overview",
                WorkableRealtimeWorkerOverviewUpdateFactory.CreateInitial(result)),
            this.Context.ConnectionAborted);
    }

    private WorkAuthorizationSnapshot CreateAuthorization(
        IWorkSystem system,
        string? systemName,
        out IWorkSystemSession session)
    {
        var requestContext = CreateRequestContext();
        EnsureCanAccessNamedSystem(system, systemName, requestContext);
        var groups = groupProvider.GetGroups(requestContext.Actor, system.Name);
        session = system.CreateSession(requestContext with
        {
            Authorization = WorkAuthorizationSnapshot.Create(
                requestContext.Actor,
                groups,
                readableDefinitionIds: null),
        });

        return WorkAuthorizationSnapshot.Create(
            requestContext.Actor,
            groups,
            session.Catalog.Definitions.Select(static definition => definition.Id));
    }

    private WorkRequestContext CreateRequestContext()
        => requestContexts.Create(
            this.Context.GetHttpContext(),
            WorkInvocationChannel.SignalR)
            .WithSurface(WorkOriginSurface.WorkableAdapter);

    private static void EnsureCanAccessNamedSystem(
        IWorkSystem system,
        string? systemName,
        WorkRequestContext requestContext)
    {
        if (string.IsNullOrWhiteSpace(systemName) || system.DescribeAccess(requestContext).HasAnyAccess())
        {
            return;
        }

        throw new WorkSystemAccessDeniedException(
            WorkSystemPermission.AccessSystem,
            system.Id,
            system.Name);
    }

    private IWorkSystem ResolveSystem(string? systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName))
        {
            return registry.Default;
        }

        return registry.TryGet(systemName, out var system)
            ? system
            : throw new HubException($"Workable system '{systemName}' was not found.");
    }

    private static WorkerId ParseWorkerId(string workerId)
        => Guid.TryParse(workerId, out var parsed)
            ? new WorkerId(parsed)
            : throw new HubException($"Worker id '{workerId}' is not valid.");
}
