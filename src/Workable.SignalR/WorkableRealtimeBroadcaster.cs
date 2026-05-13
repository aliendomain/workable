using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Workable;
internal sealed class WorkableRealtimeBroadcaster(
    IWorkSystemRegistry registry,
    IHubContext<WorkableRealtimeHub> hub,
    IOptions<WorkableSignalROptions> options) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = registry.Systems
            .Select(system => this.BroadcastSystem(system, stoppingToken))
            .ToArray();

        return tasks.Length == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    private async Task BroadcastSystem(IWorkSystem system, CancellationToken cancellationToken)
    {
        try
        {
            var dashboardSignal = new DashboardSignal();
            await using var subscription = system.Events.Subscribe(
                options: new WorkEventSubscriptionOptions(
                    options.Value.EventSubscriptionCapacity,
                    options.Value.EventOverflowBehavior));
            using var subscriptionCancellation = cancellationToken.UnsafeRegister(
                static state =>
                {
                    if (state is IWorkEventSubscription subscription)
                    {
                        _ = Task.Run(async () => await subscription.DisposeAsync());
                    }
                },
                subscription);

            await Task.WhenAll(
                this.BroadcastEvents(system, subscription, dashboardSignal, cancellationToken),
                this.BroadcastDashboard(system, dashboardSignal, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task BroadcastEvents(
        IWorkSystem system,
        IWorkEventSubscription subscription,
        DashboardSignal dashboardSignal,
        CancellationToken cancellationToken)
    {
        await foreach (var workEvent in subscription.Read(CancellationToken.None))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            dashboardSignal.MarkDirty();
            var realtimeEvent = WorkableRealtimeEvent.From(workEvent);

            await hub.Clients
                .Group(WorkableRealtimeGroups.SystemEvents(system))
                .SendAsync(WorkableRealtimeClientMethods.WorkEvent, realtimeEvent, cancellationToken);

            if (workEvent.WorkerId is { } workerId)
            {
                await hub.Clients
                    .Group(WorkableRealtimeGroups.Worker(system, workerId))
                    .SendAsync(WorkableRealtimeClientMethods.WorkEvent, realtimeEvent, cancellationToken);
            }

            if (workEvent.DefinitionId is { } definitionId)
            {
                await hub.Clients
                    .Group(WorkableRealtimeGroups.Definition(system, definitionId))
                    .SendAsync(WorkableRealtimeClientMethods.WorkEvent, realtimeEvent, cancellationToken);
            }
        }
    }

    private async Task BroadcastDashboard(
        IWorkSystem system,
        DashboardSignal dashboardSignal,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.DashboardPublishInterval);
        using var timerCancellation = cancellationToken.UnsafeRegister(
            static state =>
            {
                if (state is PeriodicTimer timer)
                {
                    timer.Dispose();
                }
            },
            timer);

        while (await timer.WaitForNextTickAsync(CancellationToken.None))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!dashboardSignal.TryConsumeDirty())
            {
                continue;
            }

            var overview = await system.Query.GetSystemOverview(cancellationToken);
            await hub.Clients
                .Group(WorkableRealtimeGroups.Dashboard(system))
                .SendAsync(
                    WorkableRealtimeClientMethods.DashboardUpdated,
                    WorkableRealtimeDashboard.From(system, overview),
                    cancellationToken);
        }
    }

    private sealed class DashboardSignal
    {
        private int isDirty;

        public void MarkDirty()
            => Volatile.Write(ref this.isDirty, 1);

        public bool TryConsumeDirty()
            => Interlocked.Exchange(ref this.isDirty, 0) == 1;
    }
}
