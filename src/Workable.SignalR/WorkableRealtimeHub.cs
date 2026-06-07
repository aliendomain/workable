using Microsoft.AspNetCore.SignalR;

namespace Workable;
public sealed class WorkableRealtimeHub(
    IWorkSystemRegistry registry,
    IWorkAuthorizationGroupProvider groupProvider,
    IWorkRequestContextFactory requestContexts,
    WorkableViewQueryAdapter views,
    WorkableRealtimeEventSubscriptions eventSubscriptions,
    WorkableRealtimeViewSubscriptions viewSubscriptions,
    WorkableRealtimeWorkerOverviewSubscriptions workerOverviewSubscriptions) : Hub
{
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
            WorkInvocationChannel.SignalR);

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
