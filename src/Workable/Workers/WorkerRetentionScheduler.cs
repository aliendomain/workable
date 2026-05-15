using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Workable;
internal sealed class WorkerRetentionScheduler(
    ConcurrentDictionary<WorkerId, WorkerRecord> workers,
    Func<WorkerRecord, long, WorkActionOutcome> purge,
    Action<WorkerRecord> publishPurgeEvent) : IDisposable
{
    private readonly PriorityQueue<ScheduledPurge, DateTimeOffset> scheduledPurges = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly Lock sync = new();
    private CancellationTokenSource? cancellation;
    private Task? schedulerTask;

    public void Start()
    {
        lock (this.sync)
        {
            if (this.schedulerTask is { IsCompleted: false })
            {
                return;
            }

            this.cancellation?.Dispose();
            this.cancellation = new CancellationTokenSource();
            using (ExecutionContext.SuppressFlow())
            {
                this.schedulerTask = Task.Run(() => this.Run(this.cancellation.Token), CancellationToken.None);
            }

            this.signal.Release();
        }
    }

    public async Task Stop(CancellationToken cancellationToken)
    {
        Task? task;
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            task = this.schedulerTask;
            this.signal.Release();
        }

        if (task is not null)
        {
            await task.WaitAsync(cancellationToken);
        }
    }

    public void Clear()
    {
        lock (this.sync)
        {
            this.scheduledPurges.Clear();
        }
    }

    public void Schedule(WorkerRecord worker)
    {
        if (!worker.IsFinal)
        {
            return;
        }

        var dueAt = DateTimeOffset.UtcNow + worker.Configuration.Retention.PurgeInterval;
        lock (this.sync)
        {
            this.scheduledPurges.Enqueue(new ScheduledPurge(worker.Id), dueAt);
            this.signal.Release();
        }
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            this.signal.Release();
            this.cancellation?.Dispose();
        }
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (this.TryTakeDuePurge(out var scheduledPurge))
                {
                    this.TryPurge(scheduledPurge);
                    continue;
                }

                var delay = this.GetDelayUntilNextPurge();
                if (delay == Timeout.InfiniteTimeSpan)
                {
                    await this.signal.WaitAsync(cancellationToken);
                }
                else
                {
                    await this.signal.WaitAsync(delay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    private bool TryTakeDuePurge([NotNullWhen(true)] out ScheduledPurge? scheduledPurge)
    {
        lock (this.sync)
        {
            if (!this.scheduledPurges.TryPeek(out var _, out var dueAt) ||
                dueAt > DateTimeOffset.UtcNow)
            {
                scheduledPurge = null;
                return false;
            }

            scheduledPurge = this.scheduledPurges.Dequeue();
            return true;
        }
    }

    private TimeSpan GetDelayUntilNextPurge()
    {
        lock (this.sync)
        {
            if (!this.scheduledPurges.TryPeek(out _, out var dueAt))
            {
                return Timeout.InfiniteTimeSpan;
            }

            var delay = dueAt - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    private void TryPurge(ScheduledPurge scheduledPurge)
    {
        if (!workers.TryGetValue(scheduledPurge.WorkerId, out var worker) || !worker.IsFinal)
        {
            return;
        }

        var outcome = purge(worker, worker.Revision);
        if (outcome.IsAccepted)
        {
            publishPurgeEvent(worker);
        }
    }

    private sealed record ScheduledPurge(WorkerId WorkerId);
}
