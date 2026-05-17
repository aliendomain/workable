using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Workable;
internal sealed class WorkableRealtimeBroadcaster(
    IWorkSystemRegistry registry,
    IHubContext<WorkableRealtimeHub> hub,
    WorkableViewQueryAdapter views,
    WorkableRealtimeViewSubscriptions viewSubscriptions,
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
            var publishSignal = new PublishSignal();
            await using var subscription = system.Events.Subscribe(
                options: new WorkEventSubscriptionOptions(
                    options.Value.EventSubscriptionCapacity,
                    options.Value.EventOverflowBehavior));

            await Task.WhenAll(
                this.BroadcastEvents(system, subscription, publishSignal, cancellationToken),
                this.BroadcastViews(system, publishSignal, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task BroadcastEvents(
        IWorkSystem system,
        IWorkEventSubscription subscription,
        PublishSignal publishSignal,
        CancellationToken cancellationToken)
    {
        await foreach (var workEvent in subscription.Read(cancellationToken))
        {
            publishSignal.MarkDirty();
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

    private async Task BroadcastViews(
        IWorkSystem system,
        PublishSignal publishSignal,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.PublishInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!publishSignal.TryConsumeDirty())
            {
                continue;
            }

            foreach (var subscription in viewSubscriptions.GetActiveSubscriptions(system))
            {
                var view = await views.View(
                    system,
                    subscription.ViewName,
                    subscription.Criteria,
                    cancellationToken: cancellationToken);
                await hub.Clients
                    .Group(subscription.GroupName)
                    .SendAsync(
                        WorkableRealtimeClientMethods.ViewUpdated,
                        view,
                        cancellationToken);
            }
        }
    }

    private sealed class PublishSignal
    {
        private int isDirty;

        public void MarkDirty()
            => Volatile.Write(ref this.isDirty, 1);

        public bool TryConsumeDirty()
            => Interlocked.Exchange(ref this.isDirty, 0) == 1;
    }
}
