using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Events")]
public sealed class WorkEventPayloadTests
{
    [Fact]
    public async Task QueueAndCompletionEventsCarryWorkerInputOutputAndStateAtEventTime()
    {
        var definition = WorkDefinition.Create("events.payload", "Publishes event payloads.");
        await using var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromJson("""{"done":true}"""))));
        await system.Start();

        await using var queuedSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.queued"));
        await using var completedSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.completed"));
        await using var queuedReader = queuedSubscription.Read().GetAsyncEnumerator();
        await using var completedReader = completedSubscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("events.payload", WorkInput.FromJson("""{"value":42}"""));
        var completion = await handle.WaitForCompletion();
        var queued = await ReadNext(queuedReader);
        var completed = await ReadNext(completedReader);

        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);

        var queuedData = RequiredData(queued);
        Assert.Equal("events.payload", queuedData.GetProperty("worker").GetProperty("definitionName").GetString());
        Assert.Equal("Queued", queuedData.GetProperty("worker").GetProperty("state").GetString());
        Assert.Equal("""{"value":42}""", queuedData.GetProperty("input").GetProperty("json").GetString());

        var completedData = RequiredData(completed);
        Assert.Equal("Completed", completedData.GetProperty("worker").GetProperty("state").GetString());
        Assert.Equal("Completed", completedData.GetProperty("completionStatus").GetString());
        Assert.Equal("""{"done":true}""", completedData.GetProperty("output").GetProperty("json").GetString());
    }

    [Fact]
    public async Task WorkerActionEventsCarryActionOutcomeAndResultingWorkerState()
    {
        var definition = WorkDefinition.Create(
            "events.action",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var handle = await system.Queue.Enqueue("events.action");
        var worker = await system.Query.Worker(RequiredWorkerId(handle))
            ?? throw new InvalidOperationException("Expected worker.");
        await using var subscription = system.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.cancel"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var outcome = await system.Workers.Execute(worker.Version, WorkAction.Cancel);
        var workEvent = await ReadNext(reader);

        var data = RequiredData(workEvent);
        Assert.True(outcome.IsAccepted);
        Assert.Equal("Cancel", data.GetProperty("action").GetString());
        Assert.Equal("Accepted", data.GetProperty("actionStatus").GetString());
        Assert.Equal("Canceled", data.GetProperty("worker").GetProperty("state").GetString());
    }

    [Fact]
    public async Task PurgeEventsCarryPurgedWorkerIdsAndDate()
    {
        var definition = WorkDefinition.Create("events.purge", "Publishes lightweight purge payloads.");
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var completed = await (await system.Queue.Enqueue("events.purge")).WaitForCompletion();
        var worker = completed.Worker ?? throw new InvalidOperationException("Expected worker.");
        await using var subscription = system.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.purge"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var outcome = await system.Workers.Execute(worker.Version, WorkAction.Purge);
        var workEvent = await ReadNext(reader);

        var data = RequiredData(workEvent);
        var workerIds = data.GetProperty("workerIds").EnumerateArray().ToArray();
        Assert.True(outcome.IsAccepted);
        Assert.Equal(worker.Id, workEvent.WorkerId);
        Assert.NotEqual(default, data.GetProperty("purgedAt").GetDateTimeOffset());
        Assert.Single(workerIds);
        Assert.Equal(worker.Id.Value, workerIds[0].GetProperty("value").GetGuid());
        Assert.False(data.TryGetProperty("worker", out _));
        Assert.False(data.TryGetProperty("action", out _));
        Assert.False(data.TryGetProperty("actionStatus", out _));
    }

    [Fact]
    public async Task RetentionPurgeEventsCanCarryMultiplePurgedWorkerIds()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        const int WorkerCount = 32;
        var definition = WorkDefinition.Create(
            "events.purge.batch",
            "Publishes batched retention purge payloads.",
            configuration: WorkConfiguration.Default with
            {
                Retention = WorkRetentionConfiguration.Default with
                {
                    PurgeInterval = TimeSpan.FromMinutes(10),
                    MaximumFinalWorkers = 1,
                },
            });
        await using var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            if (Interlocked.Increment(ref startedCount) == WorkerCount)
            {
                allStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return WorkExecutionResult.Success();
        });
        await system.Start();
        await using var subscription = system.Events.Subscribe(new WorkEventFilter(EventType: "worker.purge"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var handles = await Task.WhenAll(Enumerable.Range(0, WorkerCount).Select(_ => system.Queue.Enqueue("events.purge.batch")));
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Task.WhenAll(handles.Select(handle => handle.WaitForCompletion()));

        var workEvent = await ReadUntil(
            reader,
            workEvent => RequiredData(workEvent).GetProperty("workerIds").GetArrayLength() > 1);
        var data = RequiredData(workEvent);

        Assert.Null(workEvent.WorkerId);
        Assert.Equal(definition.Id, workEvent.DefinitionId);
        Assert.True(data.GetProperty("workerIds").GetArrayLength() > 1);
        Assert.NotEqual(default, data.GetProperty("purgedAt").GetDateTimeOffset());
        Assert.False(data.TryGetProperty("worker", out _));
        Assert.False(data.TryGetProperty("action", out _));
    }

    [Fact]
    public async Task ReconfigurationEventsCarryRequestedChangesAndResultingWorkerState()
    {
        var definition = WorkDefinition.Create(
            "events.reconfigure",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var handle = await system.Queue.Enqueue("events.reconfigure");
        var worker = await system.Query.Worker(RequiredWorkerId(handle))
            ?? throw new InvalidOperationException("Expected worker.");
        await using var subscription = system.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.reconfigured"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(ProfilingEnabled: true));
        var workEvent = await ReadNext(reader);

        var data = RequiredData(workEvent);
        Assert.True(outcome.IsAccepted);
        Assert.Equal("Accepted", data.GetProperty("reconfigurationStatus").GetString());
        Assert.True(data.GetProperty("reconfiguration").GetProperty("profilingEnabled").GetBoolean());
        Assert.True(data.GetProperty("worker").GetProperty("revision").GetInt64() > worker.Revision);
    }

    [Fact]
    public async Task RecurringIterationAndWaitingEventsCarryIterationAndWaitDetails()
    {
        var attempts = 0;
        var definition = WorkDefinition.Create(
            "events.recurring",
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(5)),
            });
        await using var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            attempts++;
            return Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromJson($$"""{"attempt":{{attempts}}}""")));
        });
        await system.Start();

        await using var iterationSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.iteration.completed"));
        await using var waitingSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.waiting"));
        await using var iterationReader = iterationSubscription.Read().GetAsyncEnumerator();
        await using var waitingReader = waitingSubscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("events.recurring");
        var workerId = RequiredWorkerId(handle);
        try
        {
            var iteration = await ReadNext(iterationReader);
            var waiting = await ReadNext(waitingReader);

            var iterationData = RequiredData(iteration);
            Assert.Equal("Completed", iterationData.GetProperty("completionStatus").GetString());
            Assert.Equal(1, iterationData.GetProperty("iteration").GetProperty("sequence").GetInt64());
            Assert.Equal("""{"attempt":1}""", iterationData.GetProperty("iteration").GetProperty("output").GetProperty("json").GetString());

            var waitingData = RequiredData(waiting);
            Assert.Equal("Waiting", waitingData.GetProperty("worker").GetProperty("state").GetString());
            Assert.Equal("00:05:00", waitingData.GetProperty("recurrenceInterval").GetString());
        }
        finally
        {
            var worker = await system.Query.Worker(workerId);
            if (worker is not null && worker.State is not WorkerState.Completed and not WorkerState.Canceled and not WorkerState.Failed)
            {
                await system.Workers.Execute(worker.Version, WorkAction.Cancel);
            }
        }
    }

    [Fact]
    public void WorkerPublisherSkipsLogPayloadConstructionWithoutSubscribers()
    {
        var stream = new WorkEventStream();
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), stream, _ => { });
        var worker = CreateWorker("events.no-subscribers.log");
        var metadata = new CyclicLogMetadata();
        metadata.Self = metadata;
        var entry = new WorkerLogEntry(
            DateTimeOffset.UtcNow,
            worker.Id,
            worker.Work.Definition.Id,
            "test",
            Microsoft.Extensions.Logging.LogLevel.Information,
            new Microsoft.Extensions.Logging.EventId(1, "cycle"),
            "log message",
            Metadata: new Dictionary<string, object?> { ["cycle"] = metadata });

        publisher.Log(worker, entry);

        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public void WorkerPublisherStillSynchronizesWithoutSubscribers()
    {
        var stream = new WorkEventStream();
        var synchronized = false;
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), stream, _ => synchronized = true);
        var worker = CreateWorker("events.no-subscribers.sync");

        publisher.Started(worker);

        Assert.True(synchronized);
        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task QueuedPublishesWithoutSynchronizingRegisteredWorker()
    {
        var stream = new WorkEventStream();
        var synchronized = false;
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), stream, _ => synchronized = true);
        var worker = CreateWorker("events.new-worker.fast-queued");
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.queued"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        publisher.Queued(worker);
        var workEvent = await ReadNext(reader);

        Assert.False(synchronized);
        Assert.Equal(worker.Id, workEvent.WorkerId);
        Assert.Equal("worker.queued", workEvent.EventType);
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker.");

    private static JsonElement RequiredData(WorkEvent workEvent)
        => workEvent.Data ?? throw new InvalidOperationException($"Expected data for event '{workEvent.EventType}'.");

    private static WorkerRecord CreateWorker(string definitionName)
    {
        var definition = WorkDefinition.Create(definitionName);
        var now = DateTimeOffset.UtcNow;
        return new WorkerRecord(
            WorkerId.New(),
            new RegisteredWork(definition, _ => new NoopExecutor(), []),
            WorkInput.Empty,
            WorkerOptions.Default,
            WorkConfiguration.Default,
            WorkOrigin.Create(WorkInvocationChannel.DotNet, description: "Test worker."),
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            createdAt: now,
            updatedAt: now);
    }

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }

    private static async Task<WorkEvent> ReadUntil(
        IAsyncEnumerator<WorkEvent> reader,
        Func<WorkEvent, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(remaining);
            if (!hasEvent)
            {
                break;
            }

            if (predicate(reader.Current))
            {
                return reader.Current;
            }
        }

        throw new TimeoutException("The expected event did not happen.");
    }

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class CyclicLogMetadata
    {
        public CyclicLogMetadata? Self { get; set; }
    }
}
