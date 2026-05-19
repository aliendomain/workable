namespace Workable.SampleHost.Demo;

public sealed class DemoTightLoopController(
    IWorkSystemRegistry registry,
    DemoSampleSystemSelection systemSelection,
    ILogger<DemoTightLoopController> logger) : IAsyncDisposable
{
    private readonly Lock sync = new();
    private CancellationTokenSource? cancellation;
    private Task? operationsTask;
    private Task? fulfillmentTask;
    private int sequence;
    private long operationsQueued;
    private long fulfillmentQueued;
    private long rejectedCount;
    private long failedCount;
    private volatile bool useTaskYield;
    private bool disposed;

    public DemoTightLoopStatus Status()
    {
        lock (this.sync)
        {
            return this.CreateStatusUnsafe();
        }
    }

    public DemoTightLoopStatus Start(DemoTightLoopRequest request)
    {
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);

            if (this.IsRunningUnsafe())
            {
                return this.CreateStatusUnsafe();
            }

            var systems = systemSelection.Current;
            if (!systems.Operations && !systems.Fulfillment)
            {
                return this.CreateStatusUnsafe();
            }

            var source = new CancellationTokenSource();
            this.cancellation = source;
            this.useTaskYield = request.UseTaskYield;

            this.operationsTask = Task.Run(() => this.RunOperations(source.Token), CancellationToken.None);
            this.fulfillmentTask = Task.Run(() => this.RunFulfillment(source.Token), CancellationToken.None);

            return this.CreateStatusUnsafe();
        }
    }

    public async Task<DemoTightLoopStatus> Stop(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source;
        Task? operations;
        Task? fulfillment;
        lock (this.sync)
        {
            source = this.cancellation;
            operations = this.operationsTask;
            fulfillment = this.fulfillmentTask;
        }

        CancelIfAvailable(source);

        var runningTasks = new[] { operations, fulfillment }.OfType<Task>().ToArray();
        if (runningTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(runningTasks).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (source?.IsCancellationRequested == true)
            {
            }
        }

        lock (this.sync)
        {
            if (ReferenceEquals(this.cancellation, source))
            {
                this.cancellation = null;
                this.operationsTask = null;
                this.fulfillmentTask = null;
            }
        }

        source?.Dispose();
        return this.Status();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? source;
        Task? operations;
        Task? fulfillment;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            source = this.cancellation;
            operations = this.operationsTask;
            fulfillment = this.fulfillmentTask;
            this.cancellation = null;
            this.operationsTask = null;
            this.fulfillmentTask = null;
        }

        CancelIfAvailable(source);

        try
        {
            await Task.WhenAll(new[] { operations, fulfillment }.OfType<Task>());
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(static inner => inner is OperationCanceledException))
        {
        }
        catch (AggregateException exception)
        {
            logger.LogWarning(exception, "Tight-loop sample queueing stopped unexpectedly.");
        }

        source?.Dispose();
    }

    private async Task RunOperations(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!systemSelection.Current.Operations)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }

            var current = Interlocked.Increment(ref this.sequence);
            await this.QueueOperations(current, cancellationToken);
        }
    }

    private async Task RunFulfillment(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!systemSelection.Current.Fulfillment)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }

            var current = Interlocked.Increment(ref this.sequence);
            await this.QueueFulfillment(current, cancellationToken);
        }
    }

    private async Task QueueOperations(int sequenceNumber, CancellationToken cancellationToken)
    {
        try
        {
            var input = WorkInput.FromValue(
                new DemoTimedInput(
                    $"tight operations #{sequenceNumber}",
                    500,
                    DiscoveredIdentifierType: "tight-loop-sequence",
                    DiscoveredIdentifierValue: sequenceNumber.ToString(),
                    UseTaskYield: this.useTaskYield),
                subjectId: new WorkSubjectId("tight-loop", "operations"),
                identifiers:
                [
                    new WorkIdentifier("sample-workload", "tight-loop"),
                    new WorkIdentifier("tight-loop-sequence", sequenceNumber.ToString()),
                ]);

            var handle = await registry.Default.Queue.Enqueue(
                "sample.demo.quick",
                input,
                cancellationToken: cancellationToken);

            this.TrackQueueOutcome(handle.QueueOutcome, DemoTightLoopSystem.Operations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Interlocked.Increment(ref this.failedCount);
            logger.LogWarning(exception, "Failed to queue operations tight-loop sample worker {SequenceNumber}.", sequenceNumber);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private async Task QueueFulfillment(int sequenceNumber, CancellationToken cancellationToken)
    {
        try
        {
            if (!registry.TryGet("fulfillment", out var system))
            {
                Interlocked.Increment(ref this.failedCount);
                return;
            }

            var input = WorkInput.FromValue(
                new DemoTimedInput(
                    $"tight fulfillment #{sequenceNumber}",
                    500,
                    DiscoveredIdentifierType: "tight-loop-sequence",
                    DiscoveredIdentifierValue: sequenceNumber.ToString(),
                    UseTaskYield: this.useTaskYield),
                subjectId: new WorkSubjectId("tight-loop", "fulfillment"),
                identifiers:
                [
                    new WorkIdentifier("sample-workload", "tight-loop"),
                    new WorkIdentifier("tight-loop-sequence", sequenceNumber.ToString()),
                ]);

            var handle = await system.Queue.Enqueue(
                "fulfillment.demo.quick",
                input,
                cancellationToken: cancellationToken);

            this.TrackQueueOutcome(handle.QueueOutcome, DemoTightLoopSystem.Fulfillment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Interlocked.Increment(ref this.failedCount);
            logger.LogWarning(exception, "Failed to queue fulfillment tight-loop sample worker {SequenceNumber}.", sequenceNumber);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private void TrackQueueOutcome(WorkQueueOutcome outcome, DemoTightLoopSystem systemName)
    {
        if (!outcome.IsAccepted)
        {
            Interlocked.Increment(ref this.rejectedCount);
            return;
        }

        if (systemName == DemoTightLoopSystem.Operations)
        {
            Interlocked.Increment(ref this.operationsQueued);
        }
        else
        {
            Interlocked.Increment(ref this.fulfillmentQueued);
        }
    }

    private DemoTightLoopStatus CreateStatusUnsafe()
    {
        var systems = systemSelection.Current;
        return new(
            this.IsRunningUnsafe(),
            this.operationsTask is { IsCompleted: false } && systems.Operations,
            this.fulfillmentTask is { IsCompleted: false } && systems.Fulfillment,
            this.useTaskYield,
            Interlocked.Read(ref this.operationsQueued),
            Interlocked.Read(ref this.fulfillmentQueued),
            Interlocked.Read(ref this.rejectedCount),
            Interlocked.Read(ref this.failedCount));
    }

    private bool IsRunningUnsafe()
        => this.operationsTask is { IsCompleted: false } ||
            this.fulfillmentTask is { IsCompleted: false };

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

public sealed record DemoTightLoopRequest(bool UseTaskYield = false);

public sealed record DemoTightLoopStatus(
    bool IsRunning,
    bool OperationsRunning,
    bool FulfillmentRunning,
    bool UseTaskYield,
    long OperationsQueued,
    long FulfillmentQueued,
    long RejectedCount,
    long FailedCount);

internal enum DemoTightLoopSystem
{
    Operations,
    Fulfillment,
}
