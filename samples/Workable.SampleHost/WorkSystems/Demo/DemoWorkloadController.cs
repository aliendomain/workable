using System.Collections.Concurrent;
using System.Diagnostics;
using Workable.SampleHost;
using Workable.SampleHost.Fulfillment;
using Workable.SampleHost.Operations;

namespace Workable.SampleHost.Demo;

public sealed class DemoWorkloadController(
    IWorkSystemRegistry registry,
    DemoSampleSystemSelection systemSelection,
    ILogger<DemoWorkloadController> logger) : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan DefaultQueueInterval = TimeSpan.FromMilliseconds(85);
    private static readonly TimeSpan MinimumQueueInterval = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan MaximumQueueInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinishedWorkerCleanupInterval = TimeSpan.FromSeconds(1);
    private const int MaximumTrackedWorkerCleanupScanCount = 10_000;
    private const int MaximumBurstWorkerCount = 10_000_000;
    private const int DefaultFailurePercentage = 8;

    private readonly Lock sync = new();
    private readonly ConcurrentDictionary<WorkerId, byte> activeDemoWorkers = [];
    private readonly string durableBurstRunId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? cancellation;
    private Task? runTask;
    private int idempotencySequence;
    private TimeSpan queueInterval = DefaultQueueInterval;
    private int sequence;
    private int failurePercentage = DefaultFailurePercentage;
    private bool disposed;

    public bool IsRunning
    {
        get
        {
            lock (this.sync)
            {
                return this.runTask is { IsCompleted: false };
            }
        }
    }

    public DemoWorkloadStatus Status()
        => this.CreateStatus();

    public int QueueIntervalMilliseconds
    {
        get
        {
            lock (this.sync)
            {
                return (int)this.queueInterval.TotalMilliseconds;
            }
        }
    }

    public DemoWorkloadStatus Start()
    {
        CancellationTokenSource? previousCancellation = null;
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);

            if (this.runTask is { IsCompleted: false })
            {
                return this.CreateStatusUnsafe(isRunning: true);
            }

            previousCancellation = this.cancellation;
            var nextCancellation = new CancellationTokenSource();
            this.cancellation = nextCancellation;
            this.runTask = Task.Run(() => this.Run(nextCancellation.Token), CancellationToken.None);
        }

        previousCancellation?.Dispose();
        return this.Status();
    }

    public DemoWorkloadStatus SetEnabledSystems(DemoWorkloadSystemsRequest request)
    {
        systemSelection.Set(request);

        return this.Status();
    }

    public DemoWorkloadStatus SetQueueInterval(int milliseconds)
    {
        var requested = TimeSpan.FromMilliseconds(milliseconds);
        var interval = requested < MinimumQueueInterval
            ? MinimumQueueInterval
            : requested > MaximumQueueInterval
                ? MaximumQueueInterval
                : requested;

        lock (this.sync)
        {
            this.queueInterval = interval;
        }

        return this.Status();
    }

    public DemoWorkloadStatus SetFailurePercentage(int percentage)
    {
        lock (this.sync)
        {
            this.failurePercentage = Math.Clamp(percentage, 0, 100);
        }

        return this.Status();
    }

    public async Task<DemoWorkloadStatus> Stop(CancellationToken cancellationToken)
        => await this.Stop(cancelTrackedWorkers: true, cancellationToken);

    private async Task<DemoWorkloadStatus> Stop(
        bool cancelTrackedWorkers,
        CancellationToken cancellationToken)
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

        if (cancelTrackedWorkers)
        {
            await this.CancelTrackedWorkers(cancellationToken);
        }

        this.activeDemoWorkers.Clear();
        return this.Status();
    }

    public async Task<DemoWorkloadStatus> Toggle(CancellationToken cancellationToken)
        => this.IsRunning ? await this.Stop(cancellationToken) : this.Start();

    public async Task<DemoBurstResult> QueueBurst(int count, CancellationToken cancellationToken)
    {
        var requestedCount = count;
        var systems = systemSelection.Current;
        if (!systems.Operations && !systems.Fulfillment)
        {
            return DemoBurstResult.Empty(requestedCount);
        }

        var workerCount = Math.Clamp(count, 1, MaximumBurstWorkerCount);
        var firstSequence = Interlocked.Add(ref this.sequence, workerCount) - workerCount + 1;
        var stopwatch = Stopwatch.StartNew();

        var queueTasks = Enumerable
            .Range(0, workerCount)
            .Select(offset => this.QueueBurstWorker(firstSequence + offset, systems, cancellationToken))
            .ToArray();

        var accepted = await Task.WhenAll(queueTasks);
        stopwatch.Stop();

        return new DemoBurstResult(
            requestedCount,
            workerCount,
            accepted.Count(wasAccepted => wasAccepted),
            accepted.Count(wasAccepted => !wasAccepted),
            stopwatch.ElapsedMilliseconds);
    }

    public async Task<DemoBurstResult> QueueDurableBurst(int count, CancellationToken cancellationToken)
    {
        var requestedCount = count;
        if (!systemSelection.Current.Operations)
        {
            return DemoBurstResult.Empty(requestedCount);
        }

        var workerCount = Math.Clamp(count, 1, MaximumBurstWorkerCount);
        var firstSequence = Interlocked.Add(ref this.sequence, workerCount) - workerCount + 1;
        var stopwatch = Stopwatch.StartNew();

        var queueTasks = Enumerable
            .Range(0, workerCount)
            .Select(offset => this.QueueDurableBurstWorker(firstSequence + offset, cancellationToken))
            .ToArray();

        var accepted = await Task.WhenAll(queueTasks);
        stopwatch.Stop();

        return new DemoBurstResult(
            requestedCount,
            workerCount,
            accepted.Count(wasAccepted => wasAccepted),
            accepted.Count(wasAccepted => !wasAccepted),
            stopwatch.ElapsedMilliseconds);
    }

    public async Task<DemoIdempotencySampleResult> QueueIdempotencyWarning(CancellationToken cancellationToken)
    {
        if (!systemSelection.Current.Operations)
        {
            return DemoIdempotencySampleResult.Skipped("Operations is disabled.");
        }

        var subjectValue = $"sample-{Interlocked.Increment(ref this.idempotencySequence)}";
        var input = WorkInput.FromValue(
            new DemoTimedInput(
                $"idempotency duplicate sample {subjectValue}",
                1_500,
                DiscoveredIdentifierType: "sample-idempotency",
                DiscoveredIdentifierValue: subjectValue),
            subjectId: new WorkSubjectId("sample-idempotency", subjectValue),
            identifiers:
            [
                new WorkIdentifier("sample-workload", "idempotency"),
                new WorkIdentifier("sample-idempotency", subjectValue),
            ]);

        try
        {
            var session = registry.Default.CreateSession("Queue idempotency sample work from the sample host.");
            var first = await session.Queue.Enqueue(
                "sample.demo.idempotent",
                input,
                cancellationToken: cancellationToken);
            if (first.QueueOutcome.IsAccepted && first.WorkerId is { } workerId)
            {
                this.activeDemoWorkers[workerId] = 0;
            }

            var duplicate = await session.Queue.Enqueue(
                "sample.demo.idempotent",
                input,
                cancellationToken: cancellationToken);
            var duplicateMessage = duplicate.QueueOutcome.Messages.FirstOrDefault();

            return new DemoIdempotencySampleResult(
                SubjectValue: subjectValue,
                AcceptedCount: (first.QueueOutcome.IsAccepted ? 1 : 0) + (duplicate.QueueOutcome.IsAccepted ? 1 : 0),
                RejectedCount: (!first.QueueOutcome.IsAccepted ? 1 : 0) + (!duplicate.QueueOutcome.IsAccepted ? 1 : 0),
                FirstStatus: first.QueueOutcome.Status.ToString(),
                SecondStatus: duplicate.QueueOutcome.Status.ToString(),
                RejectionCode: duplicateMessage?.Code,
                RejectionMessage: duplicateMessage?.Text,
                Status: "Completed",
                Message: "Queued an idempotent worker and immediately retried the same subject.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            logger.LogWarning(exception, "Failed to queue idempotency sample workload item.");
            return DemoIdempotencySampleResult.Failed("Unable to trigger the idempotency sample.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.Stop(cancelTrackedWorkers: false, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Sample workload generator encountered an error while stopping during host shutdown.");
        }
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
                logger.LogWarning(exception, "Sample workload generator stopped unexpectedly.");
            }
        }

        source?.Dispose();
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        await this.QueueRecurringWorkers(cancellationToken);
        var nextCleanupAt = DateTimeOffset.UtcNow + FinishedWorkerCleanupInterval;

        while (true)
        {
            await Task.Delay(this.GetQueueInterval(), cancellationToken);
            await this.QueueNext(cancellationToken);

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

    private async Task QueueRecurringWorkers(CancellationToken cancellationToken)
    {
        var systems = this.GetEnabledSystems();
        if (systems.Operations)
        {
            await this.QueueDefault(
                "sample.demo.recurring",
                new DemoTimedInput("operations recurring pulse", 1_200, DiscoveredIdentifierType: "demo-cycle", DiscoveredIdentifierValue: "operations"),
                new DemoRelationshipKeys(
                    Subject: new WorkSubjectId("demo-recurring", "operations"),
                    Identifier: new WorkIdentifier("sample-workload", "home-toggle")),
                cancellationToken);
        }

        if (systems.Fulfillment)
        {
            await this.QueueFulfillment(
                "fulfillment.demo.recurring",
                new DemoTimedInput("fulfillment recurring pulse", 1_400, DiscoveredIdentifierType: "demo-cycle", DiscoveredIdentifierValue: "fulfillment"),
                new DemoRelationshipKeys(
                    Subject: new WorkSubjectId("demo-recurring", "fulfillment"),
                    Identifier: new WorkIdentifier("sample-workload", "home-toggle")),
                cancellationToken);
        }
    }

    private async Task QueueNext(CancellationToken cancellationToken)
    {
        var systems = this.GetEnabledSystems();
        if (!systems.Operations && !systems.Fulfillment)
        {
            return;
        }

        var current = Interlocked.Increment(ref this.sequence);
        var lane = current % 10;

        if (systems.Operations && !systems.Fulfillment)
        {
            await this.QueueOperationsWork(lane, current, cancellationToken);
            return;
        }

        if (!systems.Operations && systems.Fulfillment)
        {
            await this.QueueFulfillmentWork(lane, current, cancellationToken);
            return;
        }

        if (current % 2 == 0)
        {
            await this.QueueOperationsWork(lane, current, cancellationToken);
            return;
        }

        await this.QueueFulfillmentWork(lane, current, cancellationToken);
    }

    private Task QueueOperationsWork(int lane, int sequenceNumber, CancellationToken cancellationToken)
    {
        if (this.ShouldFailWorker(sequenceNumber))
        {
            return this.QueueDefault(
                "sample.demo.quick",
                FailedDemoInput("operations configured failure", sequenceNumber, Random.Shared.Next(500, 2_500)),
                Mixed(sequenceNumber, "operations"),
                cancellationToken);
        }

        return this.QueueOperationsWorkUnchecked(lane, sequenceNumber, cancellationToken);
    }

    private Task QueueOperationsWorkUnchecked(int lane, int sequenceNumber, CancellationToken cancellationToken)
        => lane switch
        {
            0 => this.QueueDefault("sample.demo.long", DemoInput("operations long running", sequenceNumber, 10_000), Subject("demo-long", "operations"), cancellationToken),
            1 => this.QueueDefault("sample.demo.throttled", DemoInput("operations throttled", sequenceNumber, 6_000), Subject("demo-throttle", "operations"), cancellationToken),
            2 => this.QueueDefault("qa.validation.flaky", new FlakyValidationInput($"validation-{sequenceNumber}", ShouldFail: false, WarningCount: 2), Identifier("validation", sequenceNumber), cancellationToken),
            3 => this.QueueDefault("sample.delay", new SampleDelayInput(8_500), Subject("delay", sequenceNumber), cancellationToken),
            4 => this.QueueDefault("billing.invoice.generate", OperationsPayloads.Invoice(sequenceNumber), Subject("invoice", $"INV-DEMO-{sequenceNumber:D4}"), cancellationToken),
            5 => this.QueueDefault("analytics.report.export", OperationsPayloads.Report(sequenceNumber), Identifier("report", sequenceNumber), cancellationToken),
            6 => this.QueueDefault("data.import.csv", OperationsPayloads.Import(sequenceNumber), Subject("import-feed", "customers"), cancellationToken),
            _ => this.QueueDefault("sample.demo.quick", DemoInput("operations quick", sequenceNumber, Random.Shared.Next(500, 2_500)), Mixed(sequenceNumber, "operations"), cancellationToken),
        };

    private Task QueueFulfillmentWork(int lane, int sequenceNumber, CancellationToken cancellationToken)
    {
        if (this.ShouldFailWorker(sequenceNumber))
        {
            return this.QueueFulfillment(
                "fulfillment.demo.quick",
                FailedDemoInput("fulfillment configured failure", sequenceNumber, Random.Shared.Next(500, 2_500)),
                Mixed(sequenceNumber, "fulfillment"),
                cancellationToken);
        }

        return this.QueueFulfillmentWorkUnchecked(lane, sequenceNumber, cancellationToken);
    }

    private Task QueueFulfillmentWorkUnchecked(int lane, int sequenceNumber, CancellationToken cancellationToken)
        => lane switch
        {
            0 => this.QueueFulfillment("fulfillment.demo.long", DemoInput("fulfillment long running", sequenceNumber, 10_000), Subject("demo-long", "fulfillment"), cancellationToken),
            1 => this.QueueFulfillment("fulfillment.demo.throttled", DemoInput("fulfillment throttled", sequenceNumber, 7_500), Subject("demo-throttle", "fulfillment"), cancellationToken),
            2 => this.QueueFulfillment("fulfillment.exception.route", new FulfillmentExceptionInput($"EX-{sequenceNumber:D4}", FulfillmentExceptionType.CarrierDelay, "Synthetic sample exception.", Escalate: false), Identifier("exception", sequenceNumber), cancellationToken),
            3 => this.QueueFulfillment("shipping.rate.shop", FulfillmentPayloads.RateShop(sequenceNumber), Subject("carrier-market", "western"), cancellationToken),
            4 => this.QueueFulfillment("shipping.label.purchase", FulfillmentPayloads.Label(sequenceNumber), Subject("order", $"ORD-{sequenceNumber:D5}"), cancellationToken),
            5 => this.QueueFulfillment("warehouse.slotting.recommend", FulfillmentPayloads.Slotting(sequenceNumber), Identifier("sku", $"SKU-{sequenceNumber:D5}"), cancellationToken),
            6 => this.QueueFulfillment("procurement.reorder.submit", FulfillmentPayloads.Reorder(sequenceNumber), Subject("vendor", $"VEND-{sequenceNumber % 5:D2}"), cancellationToken),
            _ => this.QueueFulfillment("fulfillment.demo.quick", DemoInput("fulfillment quick", sequenceNumber, Random.Shared.Next(500, 2_500)), Mixed(sequenceNumber, "fulfillment"), cancellationToken),
        };

    private async Task QueueDefault<TInput>(
        string workName,
        TInput payload,
        DemoRelationshipKeys keys,
        CancellationToken cancellationToken)
        => await this.Queue(registry.Default, workName, payload, keys, cancellationToken);

    private async Task QueueFulfillment<TInput>(
        string workName,
        TInput payload,
        DemoRelationshipKeys keys,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGet("fulfillment", out var system))
        {
            return;
        }

        await this.Queue(system, workName, payload, keys, cancellationToken);
    }

    private async Task Queue<TInput>(
        IWorkSystem system,
        string workName,
        TInput payload,
        DemoRelationshipKeys keys,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = WorkInput.FromValue(
                payload,
                subjectId: keys.Subject,
                identifiers: keys.Identifier is null
                    ? [new WorkIdentifier("sample-workload", "home-toggle")]
                    : [new WorkIdentifier("sample-workload", "home-toggle"), keys.Identifier.Value]);
            var session = system.CreateSession($"Queue sample workload '{workName}' from the sample host.");
            var handle = await session.Queue.Enqueue(workName, input, cancellationToken: cancellationToken);
            if (handle.WorkerId is { } workerId)
            {
                this.activeDemoWorkers[workerId] = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to queue sample workload item {WorkName}.", workName);
        }
    }

    private async Task<bool> QueueBurstWorker(
        int sequenceNumber,
        DemoWorkloadSystems systems,
        CancellationToken cancellationToken)
    {
        try
        {
            var useFulfillment = systems.Fulfillment && (!systems.Operations || sequenceNumber % 2 == 1);
            var system = registry.Default;
            if (useFulfillment)
            {
                if (!registry.TryGet("fulfillment", out var fulfillment))
                {
                    return false;
                }

                system = fulfillment;
            }

            var systemName = useFulfillment ? "fulfillment" : "operations";
            var workName = useFulfillment ? "fulfillment.demo.quick" : "sample.demo.quick";
            var input = WorkInput.FromValue(
                new DemoTimedInput(
                    $"burst {systemName} #{sequenceNumber}",
                    500,
                    DiscoveredIdentifierType: "burst-sequence",
                    DiscoveredIdentifierValue: sequenceNumber.ToString()),
                subjectId: new WorkSubjectId("sample-burst", systemName),
                identifiers:
                [
                    new WorkIdentifier("sample-workload", "burst"),
                    new WorkIdentifier("burst-sequence", sequenceNumber.ToString()),
                ]);

            var session = system.CreateSession($"Queue burst sample workload '{workName}' from the sample host.");
            var handle = await session.Queue.Enqueue(workName, input, cancellationToken: cancellationToken);

            return handle.QueueOutcome.IsAccepted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to queue burst sample workload item {SequenceNumber}.", sequenceNumber);
            return false;
        }
    }

    private async Task<bool> QueueDurableBurstWorker(int sequenceNumber, CancellationToken cancellationToken)
    {
        try
        {
            var input = WorkInput.FromValue(
                new DemoTimedInput(
                    $"durable burst operations #{sequenceNumber}",
                    750,
                    DiscoveredIdentifierType: "durable-burst-sequence",
                    DiscoveredIdentifierValue: sequenceNumber.ToString()),
                subjectId: new WorkSubjectId("sample-durable-burst", $"{this.durableBurstRunId}:{sequenceNumber}"),
                identifiers:
                [
                    new WorkIdentifier("sample-workload", "durable-burst"),
                    new WorkIdentifier("durable-burst-sequence", sequenceNumber.ToString()),
                ]);

            var session = registry.Default.CreateSession("Queue durable-burst sample work from the sample host.");
            var handle = await session.Queue.Enqueue("sample.demo.durable", input, cancellationToken: cancellationToken);
            if (handle.QueueOutcome.IsAccepted && handle.WorkerId is { } workerId)
            {
                this.activeDemoWorkers[workerId] = 0;
            }

            return handle.QueueOutcome.IsAccepted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to queue durable burst sample workload item {SequenceNumber}.", sequenceNumber);
            return false;
        }
    }

    private DemoWorkloadStatus CreateStatus(bool? isRunning = null)
    {
        lock (this.sync)
        {
            var systems = systemSelection.Current;
            return new DemoWorkloadStatus(
                isRunning ?? (this.runTask is { IsCompleted: false }),
                this.sequence,
                this.activeDemoWorkers.Count,
                (int)this.queueInterval.TotalMilliseconds,
                systems.Operations,
                systems.Fulfillment,
                this.failurePercentage);
        }
    }

    private DemoWorkloadStatus CreateStatusUnsafe(bool? isRunning = null)
    {
        var systems = systemSelection.Current;
        return new(
            isRunning ?? (this.runTask is { IsCompleted: false }),
            this.sequence,
            this.activeDemoWorkers.Count,
            (int)this.queueInterval.TotalMilliseconds,
            systems.Operations,
            systems.Fulfillment,
            this.failurePercentage);
    }

    private static bool IsCriticalException(Exception exception)
        => exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            InvalidProgramException;

    private DemoWorkloadSystems GetEnabledSystems()
        => systemSelection.Current;

    private async Task CancelTrackedWorkers(CancellationToken cancellationToken)
    {
        foreach (var workerId in this.activeDemoWorkers.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var system in registry.Systems)
            {
                var session = system.CreateSession("Cancel tracked sample workload from the sample host.");
                var worker = await session.Query.Worker(workerId, cancellationToken: cancellationToken);
                if (worker is null)
                {
                    continue;
                }

                if (ShouldCancelWhenStoppingDemoWorkload(worker.State))
                {
                    await session.Workers.Execute(new WorkerVersion(worker.Id, worker.Revision), WorkAction.Cancel, cancellationToken);
                }

                break;
            }
        }

        await this.RemoveFinishedTrackedWorkers(cancellationToken);
    }

    private async Task RemoveFinishedTrackedWorkers(CancellationToken cancellationToken)
    {
        var scanned = 0;
        foreach (var workerId in this.activeDemoWorkers.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > MaximumTrackedWorkerCleanupScanCount)
            {
                return;
            }

            var found = false;
            foreach (var system in registry.Systems)
            {
                var session = system.CreateSession("Read tracked sample workload from the sample host.");
                var worker = await session.Query.Worker(workerId, cancellationToken: cancellationToken);
                if (worker is null)
                {
                    continue;
                }

                found = true;
                if (worker.State is WorkerState.Completed or WorkerState.Canceled or WorkerState.Failed)
                {
                    this.activeDemoWorkers.TryRemove(workerId, out _);
                }

                break;
            }

            if (!found)
            {
                this.activeDemoWorkers.TryRemove(workerId, out _);
            }
        }
    }

    private DemoTimedInput DemoInput(string scenario, int sequenceNumber, int delayMilliseconds)
        => new(
            $"{scenario} #{sequenceNumber}",
            delayMilliseconds,
            ShouldFail: false,
            DiscoveredIdentifierType: "demo-sequence",
            DiscoveredIdentifierValue: sequenceNumber.ToString());

    private static DemoTimedInput FailedDemoInput(string scenario, int sequenceNumber, int delayMilliseconds)
        => new(
            $"{scenario} #{sequenceNumber}",
            delayMilliseconds,
            ShouldFail: true,
            DiscoveredIdentifierType: "demo-sequence",
            DiscoveredIdentifierValue: sequenceNumber.ToString());

    private bool ShouldFailWorker(int sequenceNumber)
    {
        lock (this.sync)
        {
            var bucket = ((sequenceNumber % 100) + 100) % 100;
            return this.failurePercentage > 0 &&
                bucket < this.failurePercentage;
        }
    }

    private static bool ShouldCancelWhenStoppingDemoWorkload(WorkerState state)
        => state is WorkerState.Queued or WorkerState.Running or WorkerState.Waiting or WorkerState.Retrying;

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

    private static DemoRelationshipKeys Subject(string type, object value)
        => new(Subject: new WorkSubjectId(type, value.ToString() ?? string.Empty));

    private static DemoRelationshipKeys Identifier(string type, object value)
        => new(Identifier: new WorkIdentifier(type, value.ToString() ?? string.Empty));

    private static DemoRelationshipKeys Mixed(int sequenceNumber, string systemName)
        => new(
            Subject: new WorkSubjectId("demo-mixed", systemName),
            Identifier: new WorkIdentifier("sample-sequence", sequenceNumber.ToString()));
}

public sealed record DemoWorkloadStatus(
    bool IsRunning,
    int QueuedCount,
    int TrackedWorkerCount,
    int QueueIntervalMilliseconds,
    bool OperationsEnabled,
    bool FulfillmentEnabled,
    int FailurePercentage);

public sealed record DemoWorkloadIntervalRequest(int Milliseconds);

public sealed record DemoWorkloadFailureRequest(int Percentage);

public sealed record DemoWorkloadSystemsRequest(bool Operations, bool Fulfillment);

public sealed record DemoBurstRequest(int Count);

public sealed record DemoBurstResult(
    int RequestedCount,
    int SubmittedCount,
    int AcceptedCount,
    int RejectedCount,
    long ElapsedMilliseconds)
{
    public int QueuedCount => this.AcceptedCount;

    public int FailedCount => this.RejectedCount;

    public static DemoBurstResult Empty(int requestedCount)
        => new(requestedCount, 0, 0, 0, 0);
}

public sealed record DemoIdempotencySampleResult(
    string? SubjectValue,
    int AcceptedCount,
    int RejectedCount,
    string? FirstStatus,
    string? SecondStatus,
    string? RejectionCode,
    string? RejectionMessage,
    string Status,
    string Message)
{
    public static DemoIdempotencySampleResult Skipped(string message)
        => new(null, 0, 0, null, null, null, null, "Skipped", message);

    public static DemoIdempotencySampleResult Failed(string message)
        => new(null, 0, 0, null, null, null, null, "Failed", message);
}

internal sealed record DemoRelationshipKeys(
    WorkSubjectId? Subject = null,
    WorkIdentifier? Identifier = null);

public sealed record DemoWorkloadSystems(bool Operations, bool Fulfillment);

internal static class OperationsPayloads
{
    public static InvoiceGenerateInput Invoice(int sequence)
        => new(
            new CustomerReference($"CUST-{sequence:D4}", $"Sample Customer {sequence}", $"customer{sequence}@example.test"),
            [
                new InvoiceLineInput("Sample subscription", 1, 29.99m),
                new InvoiceLineInput("Usage", Random.Shared.Next(1, 8), 4.25m),
            ],
            CurrencyCode.USD,
            0.0825m,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            SendReceipt: sequence % 2 == 0);

    public static ReportExportInput Report(int sequence)
        => new(
            (ReportFormat)(sequence % 3),
            new DateRange(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7)), DateOnly.FromDateTime(DateTime.UtcNow)),
            ["revenue", "orders", "conversion"],
            IncludeCharts: sequence % 2 == 0);

    public static DataImportInput Import(int sequence)
        => new(
            new Uri($"https://example.test/imports/sample-{sequence}.csv"),
            ImportMode.Upsert,
            "sample_customers",
            ColumnMap: new Dictionary<string, string>
            {
                ["email_address"] = "email",
                ["created_date"] = "createdAt",
            });
}

internal static class FulfillmentPayloads
{
    public static CarrierRateShopInput RateShop(int sequence)
        => new(Address("Warehouse"), Address($"Customer {sequence}"), new PackageDimensions(48, 12, 8, 4), ["ups", "fedex", "usps"]);

    public static ShipmentLabelInput Label(int sequence)
        => new($"ORD-{sequence:D5}", Address($"Customer {sequence}"), new PackageDimensions(32, 10, 6, 4), ShippingServiceLevel.Ground);

    public static WarehouseSlottingInput Slotting(int sequence)
        => new($"SKU-{sequence:D5}", Random.Shared.Next(5, 250), Math.Round((decimal)Random.Shared.NextDouble() * 5 + 0.5m, 2), ["A", "B", "C"]);

    public static VendorReorderInput Reorder(int sequence)
        => new(
            $"VEND-{sequence % 5:D2}",
            [
                new ReorderLineInput($"SKU-{sequence:D5}", Random.Shared.Next(10, 150), Math.Round((decimal)Random.Shared.NextDouble() * 20 + 2, 2)),
                new ReorderLineInput($"SKU-{sequence + 1:D5}", Random.Shared.Next(10, 150), Math.Round((decimal)Random.Shared.NextDouble() * 20 + 2, 2)),
            ],
            Expedite: sequence % 3 == 0);

    private static Address Address(string name)
        => new(name, "100 Sample Way", "Seattle", "WA", "98101", "US");
}
