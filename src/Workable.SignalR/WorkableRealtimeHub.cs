using Microsoft.AspNetCore.SignalR;

namespace Workable;
public sealed class WorkableRealtimeHub(
    IWorkSystemRegistry registry,
    WorkableViewQueryAdapter views,
    WorkableRealtimeEventSubscriptions eventSubscriptions,
    WorkableRealtimeViewSubscriptions viewSubscriptions) : Hub
{
    public async Task WatchWorker(string workerId, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        try
        {
            await eventSubscriptions.WatchWorker(
                this.Context.ConnectionId,
                this.Groups,
                system,
                ParseWorkerId(workerId),
                this.Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while the event pump was starting.
        }
    }

    public async Task UnwatchWorker(string workerId, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        await eventSubscriptions.UnwatchWorker(
            this.Context.ConnectionId,
            this.Groups,
            system,
            ParseWorkerId(workerId),
            this.Context.ConnectionAborted);
    }

    public async Task WatchView(
        string viewName,
        WorkViewCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        var subscription = await viewSubscriptions.WatchView(
            this.Context.ConnectionId,
            this.Groups,
            system,
            viewName,
            views.NormalizeViewCriteria(viewName, criteria),
            this.Context.ConnectionAborted);

        await SendView(system, subscription.ViewName, subscription.Criteria, this.Clients.Caller);
    }

    public Task UnwatchView(string viewName, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        return viewSubscriptions.UnwatchView(
            this.Context.ConnectionId,
            this.Groups,
            system,
            viewName,
            this.Context.ConnectionAborted);
    }

    public async Task WatchSystem(string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        try
        {
            await eventSubscriptions.WatchSystem(
                this.Context.ConnectionId,
                this.Groups,
                system,
                this.Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (this.Context.ConnectionAborted.IsCancellationRequested)
        {
            // The client disconnected while the event pump was starting.
        }
    }

    public async Task WatchEvents(
        WorkableRealtimeEventCriteria? criteria = null,
        string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        try
        {
            await eventSubscriptions.WatchEvents(
                this.Context.ConnectionId,
                this.Groups,
                system,
                criteria,
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
        await eventSubscriptions.UnwatchEvents(
            this.Context.ConnectionId,
            this.Groups,
            system,
            criteria,
            this.Context.ConnectionAborted);
    }

    public async Task UnwatchSystem(string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        await eventSubscriptions.UnwatchSystem(
            this.Context.ConnectionId,
            this.Groups,
            system,
            this.Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await eventSubscriptions.RemoveConnection(this.Context.ConnectionId, this.Groups, this.Context.ConnectionAborted);
        await viewSubscriptions.RemoveConnection(this.Context.ConnectionId, this.Groups, this.Context.ConnectionAborted);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task SendView(
        IWorkSystem system,
        string viewName,
        WorkViewCriteria criteria,
        IClientProxy client)
    {
        var result = await views.View(
            system,
            viewName,
            criteria,
            cancellationToken: this.Context.ConnectionAborted);
        await client.SendAsync(
            WorkableRealtimeClientMethods.ViewUpdated,
            result,
            this.Context.ConnectionAborted);
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
