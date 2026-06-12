using System.Collections.Concurrent;
using SampleHost.Demo;
using Workable.SampleHost;

namespace Workable.SampleHost.Demo;

public sealed class DemoProfilingPressureController(
    IWorkSystemRegistry registry,
    IWorkCommandDispatcher commands,
    DemoSampleSystemSelection systemSelection,
    ILogger<DemoProfilingPressureController> logger) : IAsyncDisposable
{
    private const string DefinitionName = "sample.demo.profiling-lab";
    private static readonly TimeSpan DefaultQueueInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MinimumQueueInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan MaximumQueueInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FinishedWorkerCleanupInterval = TimeSpan.FromSeconds(1);
    private const int DefaultWorkersPerBurst = 4;
    private const int DefaultSectionCount = 4;
    private const int DefaultStepsPerSection = 3;
    private const int DefaultDelayMilliseconds = 35;
    private const int MaximumTrackedWorkerCleanupScanCount = 2_000;
    private const int MaximumWorkersPerBurst = 128;

    private readonly Lock sync = new();
    private readonly ConcurrentDictionary<WorkerId, byte> trackedWorkers = [];
    private CancellationTokenSource? cancellation;
    private Task? runTask;
    private TimeSpan queueInterval = DefaultQueueInterval;
    private int acceptedCount;
    private int delayMilliseconds = DefaultDelayMilliseconds;
    private int failedCount;
    private int rejectedCount;
    private int sectionCount = DefaultSectionCount;
    private int sequence;
    private int stepsPerSection = DefaultStepsPerSection;
    private int workersPerBurst = DefaultWorkersPerBurst;
    private DateTimeOffset? startedAt;
    private bool disposed;

    public DemoProfilingPressureStatus Status()
    {
        lock (this.sync)
        {
            return this.CreateStatusUnsafe();
        }
    }

    public DemoProfilingPressureStatus Start(DemoProfilingPressureRequest request)
    {
        CancellationTokenSource? previousCancellation = null;
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.ApplyRequestUnsafe(request);

            if (!systemSelection.Current.Operations)
            {
                return this.CreateStatusUnsafe(isRunning: false);
            }

            if (this.runTask is { IsCompleted: false })
            {
                return this.CreateStatusUnsafe(isRunning: true);
            }

            previousCancellation = this.cancellation;
            this.acceptedCount = 0;
            this.rejectedCount = 0;
            this.failedCount = 0;
            this.sequence = 0;
            this.startedAt = DateTimeOffset.UtcNow;
            this.trackedWorkers.Clear();

            var nextCancellation = new CancellationTokenSource();
            this.cancellation = nextCancellation;
            this.runTask = Task.Run(() => this.Run(nextCancellation.Token), CancellationToken.None);
        }

        previousCancellation?.Dispose();
        return this.Status();
    }

    public async Task<DemoProfilingPressureStatus> Stop(CancellationToken cancellationToken)
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
                logger.LogDebug("Profiling-pressure stop observed the expected background run-loop cancellation.");
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
                    this.startedAt = null;
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
            this.startedAt = null;
        }

        CancelIfAvailable(source);
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException) when (source?.IsCancellationRequested == true)
            {
                logger.LogDebug("Profiling-pressure disposal observed the expected background run-loop cancellation.");
            }
        }

        source?.Dispose();
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        var nextCleanupAt = DateTimeOffset.UtcNow + FinishedWorkerCleanupInterval;

        while (true)
        {
            if (systemSelection.Current.Operations)
            {
                await this.QueueBurst(cancellationToken);
            }

            await Task.Delay(this.GetQueueInterval(), cancellationToken);

            var now = DateTimeOffset.UtcNow;
            if (now >= nextCleanupAt)
            {
                nextCleanupAt = now + FinishedWorkerCleanupInterval;
                await this.RemoveFinishedTrackedWorkers(cancellationToken);
            }
        }
    }

    private TimeSpan GetQueueInterval()
    {
        lock (this.sync)
        {
            return this.queueInterval;
        }
    }

    private DemoProfilingPressureConfiguration GetConfiguration()
    {
        lock (this.sync)
        {
            return new DemoProfilingPressureConfiguration(
                this.workersPerBurst,
                this.sectionCount,
                this.stepsPerSection,
                this.delayMilliseconds);
        }
    }

    private void ApplyRequestUnsafe(DemoProfilingPressureRequest request)
    {
        var requestedInterval = TimeSpan.FromMilliseconds(request.QueueIntervalMilliseconds);
        this.queueInterval = requestedInterval < MinimumQueueInterval
            ? MinimumQueueInterval
            : requestedInterval > MaximumQueueInterval
                ? MaximumQueueInterval
                : requestedInterval;
        this.workersPerBurst = Math.Clamp(request.WorkersPerBurst, 1, MaximumWorkersPerBurst);
        this.sectionCount = Math.Clamp(request.SectionCount, 1, 6);
        this.stepsPerSection = Math.Clamp(request.StepsPerSection, 1, 5);
        this.delayMilliseconds = Math.Clamp(request.DelayMilliseconds, 5, 150);
    }

    private async Task QueueBurst(CancellationToken cancellationToken)
    {
        var configuration = this.GetConfiguration();
        var queueTasks = Enumerable
            .Range(0, configuration.WorkersPerBurst)
            .Select(_ => this.QueueSingleWorker(configuration, cancellationToken))
            .ToArray();
        var outcomes = await Task.WhenAll(queueTasks);

        Interlocked.Add(ref this.acceptedCount, outcomes.Count(outcome => outcome == DemoProfilingPressureQueueOutcome.Accepted));
        Interlocked.Add(ref this.rejectedCount, outcomes.Count(outcome => outcome == DemoProfilingPressureQueueOutcome.Rejected));
        Interlocked.Add(ref this.failedCount, outcomes.Count(outcome => outcome == DemoProfilingPressureQueueOutcome.Failed));
    }

    private async Task<DemoProfilingPressureQueueOutcome> QueueSingleWorker(
        DemoProfilingPressureConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref this.sequence);

        try
        {
            var input = WorkInput.FromValue(
                new DemoProfilingLabInput(
                    Scenario: $"pressure-{current:D6}",
                    SectionCount: configuration.SectionCount,
                    StepsPerSection: configuration.StepsPerSection,
                    DelayMilliseconds: configuration.DelayMilliseconds,
                    AddDiscoveredIdentifier: true),
                subjectId: new WorkSubjectId("sample-profiling-pressure", current.ToString()),
                identifiers:
                [
                    new WorkIdentifier("sample-workload", "profiling-pressure"),
                    new WorkIdentifier("profiling-pressure-sequence", current.ToString()),
                ]);

            var result = await commands.QueueWork(
                DefinitionName,
                input,
                "Queue profiling-pressure sample work from the sample host.",
                cancellationToken: cancellationToken);
            if (result.QueueOutcome?.IsAccepted == true && result.WorkerId is { } workerId)
            {
                this.trackedWorkers[workerId] = 0;
                return DemoProfilingPressureQueueOutcome.Accepted;
            }

            return DemoProfilingPressureQueueOutcome.Rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(exception, "Failed to queue profiling-pressure sample workload item {SequenceNumber}.", current);
            return DemoProfilingPressureQueueOutcome.Failed;
        }
    }

    private DemoProfilingPressureStatus CreateStatusUnsafe(bool? isRunning = null)
        => new(
            IsRunning: isRunning ?? (this.runTask is { IsCompleted: false }),
            DefinitionName: DefinitionName,
            SubmittedCount: this.acceptedCount + this.rejectedCount + this.failedCount,
            AcceptedCount: this.acceptedCount,
            RejectedCount: this.rejectedCount,
            FailedCount: this.failedCount,
            TrackedWorkerCount: this.trackedWorkers.Count,
            QueueIntervalMilliseconds: (int)this.queueInterval.TotalMilliseconds,
            WorkersPerBurst: this.workersPerBurst,
            SectionCount: this.sectionCount,
            StepsPerSection: this.stepsPerSection,
            DelayMilliseconds: this.delayMilliseconds,
            StartedAt: this.startedAt);

    private async Task CancelTrackedWorkers(CancellationToken cancellationToken)
    {
        var system = registry.Default;
        foreach (var workerId in this.trackedWorkers.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = system.CreateSession("Cancel profiling-pressure sample work from the sample host.");
            var worker = await session.Query.Worker(workerId, cancellationToken: cancellationToken);
            if (worker is null)
            {
                continue;
            }

            if (ShouldCancelWhenStopping(worker.State))
            {
                await session.Workers.Execute(
                    new WorkerVersion(worker.Id, worker.Revision),
                    WorkAction.Cancel,
                    cancellationToken);
            }
        }

        await this.RemoveFinishedTrackedWorkers(cancellationToken);
    }

    private async Task RemoveFinishedTrackedWorkers(CancellationToken cancellationToken)
    {
        var system = registry.Default;
        var scanned = 0;
        foreach (var workerId in this.trackedWorkers.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > MaximumTrackedWorkerCleanupScanCount)
            {
                return;
            }

            var session = system.CreateSession("Read profiling-pressure sample work from the sample host.");
            var worker = await session.Query.Worker(workerId, cancellationToken: cancellationToken);
            if (worker is null)
            {
                this.trackedWorkers.TryRemove(workerId, out _);
                continue;
            }

            if (worker.State is WorkerState.Completed or WorkerState.Canceled or WorkerState.Failed)
            {
                this.trackedWorkers.TryRemove(workerId, out _);
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

    private static bool IsCriticalException(Exception exception)
        => exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            InvalidProgramException;
}

public sealed record DemoProfilingPressureRequest(
    int QueueIntervalMilliseconds = 250,
    int WorkersPerBurst = 4,
    int SectionCount = 4,
    int StepsPerSection = 3,
    int DelayMilliseconds = 35);

public sealed record DemoProfilingPressureStatus(
    bool IsRunning,
    string DefinitionName,
    int SubmittedCount,
    int AcceptedCount,
    int RejectedCount,
    int FailedCount,
    int TrackedWorkerCount,
    int QueueIntervalMilliseconds,
    int WorkersPerBurst,
    int SectionCount,
    int StepsPerSection,
    int DelayMilliseconds,
    DateTimeOffset? StartedAt);

internal sealed record DemoProfilingPressureConfiguration(
    int WorkersPerBurst,
    int SectionCount,
    int StepsPerSection,
    int DelayMilliseconds);

internal enum DemoProfilingPressureQueueOutcome
{
    Accepted,
    Rejected,
    Failed,
}
