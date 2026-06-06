using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Events")]
public sealed class WorkEventPayloadTests
{
    [Fact]
    public async Task QueueAndCompletionEventsCarryThinWorkerPayloadsAndKeysAtEventTime()
    {
        var definition = WorkDefinition.Create("events.payload", "Publishes event payloads.");
        var subject = new WorkSubjectId("claim", "CLM-42");
        var concurrencyKey = new WorkConcurrencyKey("tenant", "west");
        var identifier = new WorkIdentifier("invoice", "INV-42");
        await using var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromJson("""{"done":true}"""))));
        await system.Start();

        await using var queuedSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.queued"));
        await using var completedSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.completed"));
        await using var queuedReader = queuedSubscription.Read().GetAsyncEnumerator();
        await using var completedReader = completedSubscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue(
            "events.payload",
            WorkInput.FromJson(
                """{"value":42}""",
                subjectId: subject,
                concurrencyKey: concurrencyKey,
                identifiers: [identifier]));
        var completion = await handle.WaitForCompletion();
        var queued = await ReadNext(queuedReader);
        var completed = await ReadNext(completedReader);

        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);

        var queuedData = RequiredData(queued);
        Assert.Equal("events.payload", queuedData.GetProperty("worker").GetProperty("definitionName").GetString());
        Assert.Equal("Queued", queuedData.GetProperty("worker").GetProperty("state").GetString());
        AssertEventOrigin(
            queuedData,
            WorkInvocationChannel.InProcess,
            description: null);
        AssertWorkerSummaries(queuedData, logTotal: 0, timelineTotal: 0);
        AssertThinEvent(queued, queuedData);
        AssertEventKeys(queuedData, subject, concurrencyKey, identifier);

        var completedData = RequiredData(completed);
        Assert.Equal("Completed", completedData.GetProperty("worker").GetProperty("state").GetString());
        Assert.Equal("Completed", completedData.GetProperty("completionStatus").GetString());
        var completedIteration = completedData.GetProperty("iteration");
        Assert.Equal("Completed", completedIteration.GetProperty("status").GetString());
        Assert.Equal("""{"done":true}""", completedIteration.GetProperty("output").GetProperty("json").GetString());
        Assert.False(completedData.TryGetProperty("origin", out _));
        AssertWorkerSummaries(
            completedData,
            logTotal: 0,
            timelineTotal: 1,
            userActionCount: 0,
            systemEventCount: 1,
            failureCount: 0);
        AssertThinEvent(completed, completedData);
        AssertEventKeys(completedData, subject, concurrencyKey, identifier);
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
        AssertEventOrigin(
            data,
            WorkInvocationChannel.InProcess,
            description: null);
    }

    [Fact]
    public async Task PurgeEventsCarryPurgedWorkerIdsAndDate()
    {
        var definition = WorkDefinition.Create("events.purge", "Publishes lightweight purge payloads.");
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var completed = await (await system.Queue.Enqueue(
            "events.purge",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-purge"))
                .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "north"))
                .WithIdentifier(new WorkIdentifier("invoice", "INV-purge")))).WaitForCompletion();
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
        Assert.False(data.TryGetProperty("keys", out _));
        Assert.False(data.TryGetProperty("origin", out _));
    }

    [Fact]
    public async Task RetentionPurgeEventsCanCarryMultiplePurgedWorkerIds()
    {
        var definition = WorkDefinition.Create(
            "events.purge.batch",
            "Publishes batched retention purge payloads.");
        await using var events = new WorkEventStream();
        var publisher = new WorkerEventPublisher(
            WorkSystemId.New(),
            null,
            events,
            _ => { });
        await using var subscription = events.Subscribe(new WorkEventFilter(EventType: "worker.purge"));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var workerIds = new[] { WorkerId.New(), WorkerId.New(), WorkerId.New() };

        publisher.Purged(workerIds, definition.Id, definition.Name);

        var workEvent = await ReadNext(reader);
        var data = RequiredData(workEvent);

        Assert.Null(workEvent.WorkerId);
        Assert.Equal(definition.Name, workEvent.WorkDefinitionName);
        Assert.Equal(workerIds.Length, data.GetProperty("workerIds").GetArrayLength());
        Assert.NotEqual(default, data.GetProperty("purgedAt").GetDateTimeOffset());
        Assert.False(data.TryGetProperty("worker", out _));
        Assert.False(data.TryGetProperty("action", out _));
        Assert.False(data.TryGetProperty("origin", out _));
    }

    [Fact]
    public async Task ExplicitPurgeEventsCanCarryOriginForUserDrivenRequests()
    {
        var stream = new WorkEventStream();
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), null, stream, _ => { });
        var worker = CreateWorker("events.purge.origin");
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.purge"));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var outcome = WorkActionOutcome.Accepted(WorkAction.Purge, worker.ToSnapshot());
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor(Id: "user-123", Email: "greya@example.test"),
            "Purge worker through the HTTP API.",
            "/workable/workers/123/actions/purge");

        publisher.ActionApplied(worker, outcome, requestContext);

        var workEvent = await ReadNext(reader);
        var data = RequiredData(workEvent);
        AssertEventOrigin(
            data,
            WorkInvocationChannel.HttpApi,
            "Purge worker through the HTTP API.",
            actorId: "user-123",
            actorEmail: "greya@example.test",
            urlContains: "/workable/workers/123/actions/purge");
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
        Assert.Equal(1, data.GetProperty("worker").GetProperty("configDifferenceCount").GetInt32());
        AssertEventOrigin(
            data,
            WorkInvocationChannel.InProcess,
            description: null);
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

        await using var iterationStartedSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.iteration.started"));
        await using var iterationSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.iteration.completed"));
        await using var waitingSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.waiting"));
        await using var iterationStartedReader = iterationStartedSubscription.Read().GetAsyncEnumerator();
        await using var iterationReader = iterationSubscription.Read().GetAsyncEnumerator();
        await using var waitingReader = waitingSubscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("events.recurring");
        var workerId = RequiredWorkerId(handle);
        try
        {
            var iterationStarted = await ReadNext(iterationStartedReader);
            var iteration = await ReadNext(iterationReader);
            var waiting = await ReadNext(waitingReader);

            var iterationStartedData = RequiredData(iterationStarted);
            var startedIterationPayload = iterationStartedData.GetProperty("iteration");
            Assert.Equal("worker.iteration.started", iterationStarted.EventType);
            Assert.Contains(
                iterationStartedData.GetProperty("worker").GetProperty("state").GetString(),
                new[] { "Running", "Waiting" });
            Assert.Equal(1, startedIterationPayload.GetProperty("sequence").GetInt64());
            Assert.Equal("Executing", startedIterationPayload.GetProperty("status").GetString());
            Assert.False(startedIterationPayload.TryGetProperty("output", out _));
            Assert.False(startedIterationPayload.TryGetProperty("messages", out _));
            Assert.False(startedIterationPayload.TryGetProperty("log", out _));
            Assert.False(startedIterationPayload.TryGetProperty("logs", out _));
            AssertThinEvent(iterationStarted, iterationStartedData);

            var iterationData = RequiredData(iteration);
            Assert.Equal("Completed", iterationData.GetProperty("completionStatus").GetString());
            var iterationPayload = iterationData.GetProperty("iteration");
            Assert.Equal(1, iterationPayload.GetProperty("sequence").GetInt64());
            Assert.Equal("Completed", iterationPayload.GetProperty("status").GetString());
            Assert.True(iterationPayload.TryGetProperty("output", out var iterationOutput));
            Assert.Equal("""{"attempt":1}""", iterationOutput.GetProperty("json").GetString());
            Assert.False(iterationPayload.TryGetProperty("failure", out _));
            Assert.False(iterationPayload.TryGetProperty("messages", out _));
            Assert.False(iterationPayload.TryGetProperty("log", out _));
            Assert.False(iterationPayload.TryGetProperty("logs", out _));
            AssertThinEvent(iteration, iterationData);

            var waitingData = RequiredData(waiting);
            Assert.Equal("Waiting", waitingData.GetProperty("worker").GetProperty("state").GetString());
            Assert.Equal("00:05:00", waitingData.GetProperty("recurrenceInterval").GetString());
            Assert.Equal(1, waitingData.GetProperty("iteration").GetProperty("sequence").GetInt64());
            Assert.Equal("Completed", waitingData.GetProperty("iteration").GetProperty("status").GetString());
            AssertWorkerSummaries(
                waitingData,
                logTotal: 0,
                timelineTotal: 2,
                userActionCount: 0,
                systemEventCount: 2,
                failureCount: 0);
            AssertThinEvent(waiting, waitingData);
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
    public async Task RetryingEventsCarryRetryAttemptOnWorkerPayload()
    {
        var attempts = 0;
        var definition = WorkDefinition.Create(
            "events.retrying",
            configuration: WorkConfiguration.Default with
            {
                TransientRetry = WorkTransientRetryConfiguration.Default with
                {
                    Count = 1,
                    InitialDelay = TimeSpan.FromMilliseconds(50),
                    MaximumDelay = TimeSpan.FromMilliseconds(50),
                    Jitter = TimeSpan.Zero,
                },
            });
        await using var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            attempts++;
            if (attempts == 1)
            {
                context.Fail("events.retrying.transient", "Retry me once.", transient: true);
                return Task.FromResult(WorkExecutionResult.Success());
            }

            return Task.FromResult(WorkExecutionResult.Success());
        });
        await system.Start();

        await using var retryingSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.retrying"));
        await using var startedSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.started"));
        await using var iterationStartedSubscription = system.Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.iteration.started"));
        await using var retryingReader = retryingSubscription.Read().GetAsyncEnumerator();
        await using var startedReader = startedSubscription.Read().GetAsyncEnumerator();
        await using var iterationStartedReader = iterationStartedSubscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("events.retrying");
        var firstStarted = await ReadNext(startedReader);
        var firstIterationStarted = await ReadNext(iterationStartedReader);
        var retryingEvent = await ReadNext(retryingReader);
        var secondIterationStarted = await ReadNext(iterationStartedReader);
        await handle.WaitForCompletion();

        var startedData = RequiredData(firstStarted);
        Assert.Equal("worker.started", firstStarted.EventType);
        Assert.Equal("Running", startedData.GetProperty("worker").GetProperty("state").GetString());
        Assert.Equal(1, startedData.GetProperty("iteration").GetProperty("sequence").GetInt64());
        Assert.Equal("Executing", startedData.GetProperty("iteration").GetProperty("status").GetString());
        Assert.Equal(1, startedData.GetProperty("iteration").GetProperty("attemptCount").GetInt32());

        var firstIterationStartedData = RequiredData(firstIterationStarted);
        Assert.Equal("worker.iteration.started", firstIterationStarted.EventType);
        Assert.Equal(1, firstIterationStartedData.GetProperty("iteration").GetProperty("sequence").GetInt64());
        Assert.Equal(1, firstIterationStartedData.GetProperty("iteration").GetProperty("attemptCount").GetInt32());

        var retryingData = RequiredData(retryingEvent);
        Assert.Equal("Retrying", retryingData.GetProperty("worker").GetProperty("state").GetString());
        Assert.Equal(1, retryingData.GetProperty("worker").GetProperty("retryAttempt").GetInt32());
        Assert.Equal("00:00:00.0500000", retryingData.GetProperty("retryDelay").GetString());
        Assert.Equal(1, retryingData.GetProperty("iteration").GetProperty("sequence").GetInt64());
        Assert.Equal("Failed", retryingData.GetProperty("iteration").GetProperty("status").GetString());
        var retryingFailure = retryingData.GetProperty("iteration").GetProperty("failure");
        Assert.Equal("Failure", retryingFailure.GetProperty("kind").GetString());
        Assert.Equal("Retry me once.", retryingFailure.GetProperty("message").GetString());
        Assert.Equal("events.retrying.transient", retryingFailure.GetProperty("code").GetString());
        Assert.False(retryingData.GetProperty("iteration").TryGetProperty("output", out _));
        AssertWorkerSummaries(
            retryingData,
            logTotal: 0,
            timelineTotal: 1,
            userActionCount: 0,
            systemEventCount: 0,
            failureCount: 1);

        var secondIterationStartedData = RequiredData(secondIterationStarted);
        Assert.Equal("worker.iteration.started", secondIterationStarted.EventType);
        Assert.Equal(2, secondIterationStartedData.GetProperty("iteration").GetProperty("sequence").GetInt64());
        Assert.Equal(2, secondIterationStartedData.GetProperty("iteration").GetProperty("attemptCount").GetInt32());

        AssertNoQueuedEvents(startedSubscription);
    }

    [Fact]
    public void WorkerPublisherSkipsLogPayloadConstructionWithoutSubscribers()
    {
        var stream = new WorkEventStream();
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), null, stream, _ => { });
        var worker = CreateWorker("events.no-subscribers.log");
        var entry = new WorkerLogEntry(
            DateTimeOffset.UtcNow,
            worker.Id,
            worker.Work.Definition.Id,
            "test",
            Microsoft.Extensions.Logging.LogLevel.Information,
            new Microsoft.Extensions.Logging.EventId(1, "cycle"),
            "log message");

        publisher.Log(worker, entry);

        Assert.Equal(0, stream.ActiveSubscriptionCount);
    }

    [Fact]
    public async Task WorkerLogEventsCarryCapturedLogPayload()
    {
        var stream = new WorkEventStream();
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), null, stream, _ => { });
        var worker = CreateWorker("events.thin-log");
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.log"));
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var entry = new WorkerLogEntry(
            DateTimeOffset.UtcNow,
            worker.Id,
            worker.Work.Definition.Id,
            "test",
            Microsoft.Extensions.Logging.LogLevel.Information,
            new Microsoft.Extensions.Logging.EventId(1, "event"),
            "log message");

        publisher.Log(worker, entry);
        var workEvent = await ReadNext(reader);
        var data = RequiredData(workEvent);
        var log = data.GetProperty("log");

        Assert.Equal("worker.log", workEvent.EventType);
        Assert.Equal("events.thin-log", data.GetProperty("worker").GetProperty("definitionName").GetString());
        Assert.False(data.GetProperty("worker").TryGetProperty("origin", out _));
        Assert.Equal("test", log.GetProperty("category").GetString());
        Assert.Equal(entry.Id.ToString("N"), log.GetProperty("id").GetString());
        Assert.Equal("Information", log.GetProperty("level").GetString());
        Assert.Equal(1, log.GetProperty("eventId").GetProperty("id").GetInt32());
        Assert.Equal("event", log.GetProperty("eventId").GetProperty("name").GetString());
        Assert.Equal("log message", log.GetProperty("message").GetString());
        Assert.False(log.TryGetProperty("metadata", out _));
        AssertThinEvent(workEvent, data);
    }

    [Fact]
    public async Task RetainedSummariesTrackTrimmedLogsForgottenIterationsAndWaitingRows()
    {
        var configuration = WorkConfiguration.Default with
        {
            Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)) with
            {
                RetainedIterations = 1,
            },
            Logging = WorkLoggingConfiguration.Default with
            {
                Level = Microsoft.Extensions.Logging.LogLevel.Trace,
                MaximumBufferedEntries = 2,
            },
        };
        var stream = new WorkEventStream();
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), null, stream, _ => { });
        var worker = CreateWorker("events.retained-summaries", configuration);
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.waiting"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        Assert.True(worker.Start(worker.Revision, advancesRevision: false, out _, CancellationToken.None).IsAccepted);
        worker.RecordLog(CreateLogEntry(worker, Microsoft.Extensions.Logging.LogLevel.Information, "info"));
        worker.RecordLog(CreateLogEntry(worker, Microsoft.Extensions.Logging.LogLevel.Error, "error"));
        worker.RecordLog(CreateLogEntry(worker, Microsoft.Extensions.Logging.LogLevel.Critical, "critical"));
        worker.CompleteRecurringIteration(WorkExecutionResult.Success(), continueRecurrence: true);

        Assert.True(worker.TryBeginNextRecurringIteration());
        worker.RecordLog(CreateLogEntry(worker, Microsoft.Extensions.Logging.LogLevel.Warning, "warning"));
        worker.CompleteRecurringIteration(WorkExecutionResult.Success(), continueRecurrence: true);

        publisher.Waiting(worker);
        var workEvent = await ReadNext(reader);
        var data = RequiredData(workEvent);

        AssertWorkerSummariesMatchSnapshot(worker.ToSnapshot(), data);
        Assert.Equal(1, data.GetProperty("worker").GetProperty("logSummary").GetProperty("total").GetInt32());
        Assert.Equal(1, data.GetProperty("worker").GetProperty("logSummary").GetProperty("warning").GetInt32());
        Assert.Equal(2, data.GetProperty("worker").GetProperty("timelineSummary").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task RetainedTimelineSummariesSuppressCurrentPausedStateRows()
    {
        var stream = new WorkEventStream();
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), null, stream, _ => { });
        var worker = CreateWorker("events.timeline-summaries");
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        await using var subscription = stream.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.pause"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        Assert.True(worker.Start(worker.Revision, advancesRevision: false, out _, CancellationToken.None).IsAccepted);
        var outcome = worker.RequestPause(worker.Revision);
        worker.RecordActionHistory(outcome, requestContext);

        publisher.ActionApplied(worker, outcome, requestContext);
        var workEvent = await ReadNext(reader);
        var data = RequiredData(workEvent);

        AssertWorkerSummariesMatchSnapshot(worker.ToSnapshot(), data);
        Assert.Equal(2, data.GetProperty("worker").GetProperty("timelineSummary").GetProperty("total").GetInt32());
        Assert.Equal(2, data.GetProperty("worker").GetProperty("timelineSummary").GetProperty("systemEventCount").GetInt32());
        Assert.Equal(0, data.GetProperty("worker").GetProperty("timelineSummary").GetProperty("failureCount").GetInt32());
    }

    [Fact]
    public void WorkerPublisherStillSynchronizesWithoutSubscribers()
    {
        var stream = new WorkEventStream();
        var synchronized = false;
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), null, stream, _ => synchronized = true);
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
        var publisher = new WorkerEventPublisher(WorkSystemId.New(), null, stream, _ => synchronized = true);
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

    private static void AssertThinEvent(WorkEvent workEvent, JsonElement data)
    {
        Assert.False(data.TryGetProperty("input", out _));
        Assert.False(data.TryGetProperty("output", out _));
        Assert.False(data.TryGetProperty("messages", out _));
        Assert.False(data.TryGetProperty("logs", out _));
    }

    private static void AssertEventOrigin(
        JsonElement data,
        WorkInvocationChannel channel,
        string? description,
        string? actorId = null,
        string? actorEmail = null,
        string? urlContains = null)
    {
        var origin = data.GetProperty("origin");
        Assert.Equal(channel.ToString(), origin.GetProperty("channel").GetString());
        if (description is null)
        {
            Assert.False(origin.TryGetProperty("description", out _));
        }
        else
        {
            Assert.Equal(description, origin.GetProperty("description").GetString());
        }

        if (urlContains is null)
        {
            Assert.False(origin.TryGetProperty("url", out _));
        }
        else
        {
            Assert.Contains(urlContains, origin.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
        }

        if (actorId is null && actorEmail is null)
        {
            Assert.False(origin.TryGetProperty("actor", out _));
            return;
        }

        var actor = origin.GetProperty("actor");
        Assert.Equal(actorId, actor.GetProperty("id").GetString());
        Assert.Equal(actorEmail, actor.GetProperty("email").GetString());
    }

    private static void AssertEventKeys(
        JsonElement data,
        WorkSubjectId subject,
        WorkConcurrencyKey concurrencyKey,
        WorkIdentifier identifier)
    {
        var keys = data.GetProperty("keys").EnumerateArray().ToArray();
        Assert.Contains(keys, key => KeyEquals(key, "subject", subject.Type, subject.Value));
        Assert.Contains(keys, key => KeyEquals(key, "concurrencyKey", concurrencyKey.Type, concurrencyKey.Value));
        Assert.Contains(keys, key => KeyEquals(key, "identifier", identifier.Type, identifier.Value));
    }

    private static bool KeyEquals(JsonElement key, string kind, string type, string value)
        => string.Equals(key.GetProperty("kind").GetString(), kind, StringComparison.OrdinalIgnoreCase) &&
            key.GetProperty("type").GetString() == type &&
            key.GetProperty("value").GetString() == value;

    private static void AssertWorkerSummaries(
        JsonElement data,
        int logTotal,
        int timelineTotal,
        int? userActionCount = null,
        int? systemEventCount = null,
        int? failureCount = null)
    {
        var worker = data.GetProperty("worker");
        Assert.Equal(logTotal, worker.GetProperty("logSummary").GetProperty("total").GetInt32());
        Assert.Equal(timelineTotal, worker.GetProperty("timelineSummary").GetProperty("total").GetInt32());
        if (userActionCount.HasValue)
        {
            Assert.Equal(userActionCount.Value, worker.GetProperty("timelineSummary").GetProperty("userActionCount").GetInt32());
        }

        if (systemEventCount.HasValue)
        {
            Assert.Equal(systemEventCount.Value, worker.GetProperty("timelineSummary").GetProperty("systemEventCount").GetInt32());
        }

        if (failureCount.HasValue)
        {
            Assert.Equal(failureCount.Value, worker.GetProperty("timelineSummary").GetProperty("failureCount").GetInt32());
        }
    }

    private static void AssertWorkerSummariesMatchSnapshot(WorkerSnapshot snapshot, JsonElement data)
    {
        var logs = snapshot.GetMergedIterations()
            .SelectMany(iteration => iteration.Logs)
            .ToArray();
        var activity = snapshot.GetActivityEvents();
        var waitingRowCount = snapshot.State == WorkerState.Waiting ? 1 : 0;
        var logSummary = data.GetProperty("worker").GetProperty("logSummary");
        var timelineSummary = data.GetProperty("worker").GetProperty("timelineSummary");

        Assert.Equal(logs.Length, logSummary.GetProperty("total").GetInt32());
        Assert.Equal(logs.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Critical), logSummary.GetProperty("critical").GetInt32());
        Assert.Equal(logs.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Error), logSummary.GetProperty("error").GetInt32());
        Assert.Equal(logs.Count(entry => entry.Level is Microsoft.Extensions.Logging.LogLevel.Error or Microsoft.Extensions.Logging.LogLevel.Critical), logSummary.GetProperty("errors").GetInt32());
        Assert.Equal(logs.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning), logSummary.GetProperty("warning").GetInt32());
        Assert.Equal(logs.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning), logSummary.GetProperty("warnings").GetInt32());
        Assert.Equal(logs.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Information), logSummary.GetProperty("information").GetInt32());
        Assert.Equal(logs.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Debug), logSummary.GetProperty("debug").GetInt32());
        Assert.Equal(logs.Count(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Trace), logSummary.GetProperty("trace").GetInt32());

        Assert.Equal(activity.Count + waitingRowCount, timelineSummary.GetProperty("total").GetInt32());
        Assert.Equal(activity.Count(item => item.Category == WorkerActivityEventCategory.UserAction), timelineSummary.GetProperty("userActionCount").GetInt32());
        Assert.Equal(activity.Count(item => item.Category == WorkerActivityEventCategory.SystemEvent) + waitingRowCount, timelineSummary.GetProperty("systemEventCount").GetInt32());
        Assert.Equal(activity.Count(item => item.Category == WorkerActivityEventCategory.Failure), timelineSummary.GetProperty("failureCount").GetInt32());
    }

    private static WorkerRecord CreateWorker(string definitionName)
        => CreateWorker(definitionName, WorkConfiguration.Default);

    private static WorkerRecord CreateWorker(string definitionName, WorkConfiguration configuration)
    {
        var definition = WorkDefinition.Create(definitionName, configuration: configuration);
        var now = DateTimeOffset.UtcNow;
        return new WorkerRecord(
            WorkerId.New(),
            new RegisteredWork(definition, _ => new NoopExecutor(), []),
            WorkInput.Empty,
            WorkerOptions.Default,
            configuration,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            createdAt: now,
            updatedAt: now);
    }

    private static WorkerLogEntry CreateLogEntry(
        WorkerRecord worker,
        Microsoft.Extensions.Logging.LogLevel level,
        string message)
        => new(
            DateTimeOffset.UtcNow,
            worker.Id,
            worker.Work.Definition.Id,
            "tests",
            level,
            new Microsoft.Extensions.Logging.EventId(1, message),
            message);

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

    private static void AssertNoQueuedEvents(IWorkEventSubscription subscription)
    {
        var diagnostics = Assert
            .IsAssignableFrom<IWorkEventSubscriptionDiagnostics>(subscription)
            .GetDiagnosticsSnapshot();

        Assert.Equal(0, diagnostics.QueuedCount);
        Assert.Equal(diagnostics.AcceptedEventCount, diagnostics.DeliveredEventCount);
    }

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}







