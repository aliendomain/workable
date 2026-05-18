using System.Collections.Concurrent;

namespace Workable.SampleHost.Demo;

public sealed class DemoQueuePressureController(
    IWorkSystemRegistry registry,
    DemoSampleSystemSelection systemSelection,
    ILogger<DemoQueuePressureController> logger) : IAsyncDisposable
{
    private const string DefinitionName = "sample.demo.queue-pressure";
    private static readonly TimeSpan QueueInterval = TimeSpan.FromMilliseconds(250);
    private const int WorkerDelayMilliseconds = 1_000;

    private readonly Lock sync = new();
    private readonly ConcurrentDictionary<WorkerId, byte> trackedWorkers = [];
    private CancellationTokenSource? cancellation;
    private Task? runTask;
    private int sequence;
    private bool disposed;

    public DemoQueuePressureStatus Status()
        => new(
            this.IsRunning,
            DefinitionName,
            this.sequence,
            this.trackedWorkers.Count,
            (int)QueueInterval.TotalMilliseconds,
            WorkerDelayMilliseconds);

    public DemoQueuePressureStatus Start()
    {
        CancellationTokenSource? previousCancellation = null;
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);

            if (!systemSelection.Current.Operations)
            {
                return new DemoQueuePressureStatus(
                    false,
                    DefinitionName,
                    this.sequence,
                    this.trackedWorkers.Count,
                    (int)QueueInterval.TotalMilliseconds,
                    WorkerDelayMilliseconds);
            }

            if (this.runTask is { IsCompleted: false })
            {
                return new DemoQueuePressureStatus(
                    true,
                    DefinitionName,
                    this.sequence,
                    this.trackedWorkers.Count,
                    (int)QueueInterval.TotalMilliseconds,
                    WorkerDelayMilliseconds);
            }

            previousCancellation = this.cancellation;
            var nextCancellation = new CancellationTokenSource();
            this.cancellation = nextCancellation;
            this.runTask = Task.Run(() => this.Run(nextCancellation.Token), CancellationToken.None);
        }

        previousCancellation?.Dispose();
        return this.Status();
    }

    public async Task<DemoQueuePressureStatus> Stop(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source;
        Task? task;
        lock (this.sync)
        {
            source = this.cancellation;
            task = this.runTask;
        }

        CancelIfAvailable(source);

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (source?.IsCancellationRequested == true)
            {
            }
        }

        if (task is not null && task.IsCompleted)
        {
            lock (this.sync)
            {
                if (ReferenceEquals(this.runTask, task))
                {
                    this.runTask = null;
                    this.cancellation = null;
                }
            }

            source?.Dispose();
        }

        await this.CancelTrackedWorkers(cancellationToken);
        this.trackedWorkers.Clear();
        return this.Status();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? source;
        Task? task;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            source = this.cancellation;
            task = this.runTask;
            this.cancellation = null;
            this.runTask = null;
        }

        CancelIfAvailable(source);
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Queue pressure sample stopped unexpectedly.");
            }
        }

        source?.Dispose();
    }

    private bool IsRunning
    {
        get
        {
            lock (this.sync)
            {
                return this.runTask is { IsCompleted: false };
            }
        }
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (systemSelection.Current.Operations)
            {
                await this.QueueNext(cancellationToken);
            }

            await Task.Delay(QueueInterval, cancellationToken);
        }
    }

    private async Task QueueNext(CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref this.sequence);
        try
        {
            var input = WorkInput.FromValue(
                new DemoTimedInput(
                    $"queue pressure #{current}",
                    WorkerDelayMilliseconds,
                    DiscoveredIdentifierType: "queue-pressure-sequence",
                    DiscoveredIdentifierValue: current.ToString()),
                concurrencyKey: new WorkConcurrencyKey("queue-pressure", "default"),
                identifiers:
                [
                    new WorkIdentifier("sample-workload", "queue-pressure"),
                    new WorkIdentifier("queue-pressure-sequence", current.ToString()),
                ]);

            var handle = await registry.Default.Queue.Enqueue(
                DefinitionName,
                input,
                cancellationToken: cancellationToken);
            if (handle.WorkerId is { } workerId)
            {
                this.trackedWorkers[workerId] = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to queue pressure sample worker {SequenceNumber}.", current);
        }
    }

    private async Task CancelTrackedWorkers(CancellationToken cancellationToken)
    {
        foreach (var workerId in this.trackedWorkers.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worker = await registry.Default.Query.Worker(workerId, cancellationToken: cancellationToken);
            if (worker is null)
            {
                continue;
            }

            if (ShouldCancelWhenStopping(worker.State))
            {
                await registry.Default.Workers.Execute(
                    new WorkerVersion(worker.Id, worker.Revision),
                    WorkAction.Cancel,
                    cancellationToken);
            }
        }
    }

    private static bool ShouldCancelWhenStopping(WorkerState state)
        => state is WorkerState.Queued
            or WorkerState.Running
            or WorkerState.Waiting
            or WorkerState.Retrying
            or WorkerState.Pausing
            or WorkerState.Canceling;

    private static bool CancelIfAvailable(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}

public sealed record DemoQueuePressureStatus(
    bool IsRunning,
    string DefinitionName,
    int QueuedCount,
    int TrackedWorkerCount,
    int QueueIntervalMilliseconds,
    int WorkerDelayMilliseconds);
