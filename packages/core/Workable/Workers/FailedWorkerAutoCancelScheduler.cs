using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Workable;

internal readonly record struct FailedWorkerAutoCancelSchedule(
    WorkerId WorkerId,
    long StateSequence,
    DateTimeOffset DueAt);

internal sealed class FailedWorkerAutoCancelScheduler(
    Func<IReadOnlyList<FailedWorkerAutoCancelSchedule>, int> autoCancel,
    ILogger? logger = null) : IDisposable
{
    private const int AutoCancelBatchSize = 4096;
    private readonly PriorityQueue<FailedWorkerAutoCancelSchedule, DateTimeOffset> scheduledAutoCancels = new();
    private readonly Dictionary<WorkerId, FailedWorkerAutoCancelSchedule> scheduledAutoCancelsByWorkerId = [];
    private readonly SemaphoreSlim signal = new(0);
    private readonly Lock sync = new();
    private readonly ILogger logger = logger ?? NullLogger.Instance;
    private CancellationTokenSource? cancellation;
    private Task? schedulerTask;
    private bool signalPending;

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
        }

        this.Signal();
    }

    public async Task Stop(CancellationToken cancellationToken)
    {
        Task? task;
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            task = this.schedulerTask;
        }

        this.Signal();

        if (task is not null)
        {
            await task.WaitAsync(cancellationToken);
        }
    }

    public void Clear()
    {
        lock (this.sync)
        {
            this.scheduledAutoCancels.Clear();
            this.scheduledAutoCancels.TrimExcess();
            this.scheduledAutoCancelsByWorkerId.Clear();
        }
    }

    public void Schedule(WorkerRecord worker)
    {
        var schedule = worker.GetFailedWorkerAutoCancelSchedule();
        if (schedule is null)
        {
            this.Forget(worker.Id);
            return;
        }

        lock (this.sync)
        {
            this.scheduledAutoCancelsByWorkerId[worker.Id] = schedule.Value;
            this.scheduledAutoCancels.Enqueue(schedule.Value, schedule.Value.DueAt);
        }

        this.Signal();
    }

    public void Forget(WorkerId workerId)
    {
        lock (this.sync)
        {
            this.scheduledAutoCancelsByWorkerId.Remove(workerId);
        }
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            this.cancellation?.Dispose();
        }

        this.Signal();
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (this.TryTakeDueBatch(out var dueAutoCancels))
                {
                    this.TryAutoCancel(dueAutoCancels, cancellationToken);
                    continue;
                }

                await this.WaitForSignal(this.GetDelayUntilNextAutoCancel(), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    private bool TryTakeDueBatch([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<FailedWorkerAutoCancelSchedule>? schedules)
    {
        lock (this.sync)
        {
            if (!this.scheduledAutoCancels.TryPeek(out _, out var dueAt) || dueAt > DateTimeOffset.UtcNow)
            {
                schedules = null;
                return false;
            }

            var dueSchedules = new List<FailedWorkerAutoCancelSchedule>(AutoCancelBatchSize);
            while (dueSchedules.Count < AutoCancelBatchSize &&
                this.scheduledAutoCancels.TryPeek(out var scheduledAutoCancel, out dueAt) &&
                dueAt <= DateTimeOffset.UtcNow)
            {
                this.scheduledAutoCancels.Dequeue();
                if (!this.scheduledAutoCancelsByWorkerId.TryGetValue(scheduledAutoCancel.WorkerId, out var currentSchedule) ||
                    currentSchedule.StateSequence != scheduledAutoCancel.StateSequence ||
                    currentSchedule.DueAt != scheduledAutoCancel.DueAt)
                {
                    continue;
                }

                this.scheduledAutoCancelsByWorkerId.Remove(scheduledAutoCancel.WorkerId);
                dueSchedules.Add(scheduledAutoCancel);
            }

            if (dueSchedules.Count == 0)
            {
                schedules = null;
                return false;
            }

            schedules = dueSchedules;
            return true;
        }
    }

    private TimeSpan GetDelayUntilNextAutoCancel()
    {
        lock (this.sync)
        {
            if (!this.scheduledAutoCancels.TryPeek(out _, out var dueAt))
            {
                return Timeout.InfiniteTimeSpan;
            }

            var delay = dueAt - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    private async Task WaitForSignal(TimeSpan delay, CancellationToken cancellationToken)
    {
        var signaled = delay == Timeout.InfiniteTimeSpan
            ? await this.WaitForSignal(cancellationToken)
            : await this.signal.WaitAsync(delay, cancellationToken);

        if (!signaled)
        {
            return;
        }

        lock (this.sync)
        {
            this.signalPending = false;
        }
    }

    private async Task<bool> WaitForSignal(CancellationToken cancellationToken)
    {
        await this.signal.WaitAsync(cancellationToken);
        return true;
    }

    private void TryAutoCancel(
        IReadOnlyList<FailedWorkerAutoCancelSchedule> schedules,
        CancellationToken cancellationToken)
    {
        try
        {
            autoCancel(schedules);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "Failed worker auto-cancel scheduler run failed.");
        }
    }

    private void Signal()
    {
        var shouldRelease = false;
        lock (this.sync)
        {
            if (!this.signalPending)
            {
                this.signalPending = true;
                shouldRelease = true;
            }
        }

        if (shouldRelease)
        {
            this.signal.Release();
        }
    }
}
