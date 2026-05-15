using Microsoft.AspNetCore.SignalR;

namespace Workable;
public sealed class WorkableRealtimeHub(IWorkSystemRegistry registry) : Hub
{
    public async Task WatchWorker(string workerId, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        await this.Groups.AddToGroupAsync(
            this.Context.ConnectionId,
            WorkableRealtimeGroups.Worker(system, ParseWorkerId(workerId)));
    }

    public async Task UnwatchWorker(string workerId, string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        await this.Groups.RemoveFromGroupAsync(
            this.Context.ConnectionId,
            WorkableRealtimeGroups.Worker(system, ParseWorkerId(workerId)));
    }

    public async Task WatchDashboard(string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        await this.Groups.AddToGroupAsync(this.Context.ConnectionId, WorkableRealtimeGroups.Dashboard(system));
        await SendDashboard(system, this.Clients.Caller);
    }

    public async Task UnwatchDashboard(string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        await this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, WorkableRealtimeGroups.Dashboard(system));
    }

    public async Task WatchSystem(string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        await this.Groups.AddToGroupAsync(this.Context.ConnectionId, WorkableRealtimeGroups.SystemEvents(system));
    }

    public async Task UnwatchSystem(string? systemName = null)
    {
        var system = ResolveSystem(systemName);
        await this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, WorkableRealtimeGroups.SystemEvents(system));
    }

    private async Task SendDashboard(IWorkSystem system, IClientProxy client)
    {
        var details = await system.Query.SystemDetails(cancellationToken: this.Context.ConnectionAborted);
        await client.SendAsync(
            WorkableRealtimeClientMethods.DashboardUpdated,
            WorkableRealtimeDashboard.From(system, details),
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
