using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "IterationStatusStream")]
public sealed class WorkIterationStatusStreamTests
{
    [Fact]
    public async Task ExecutorStatusItemsAreOrderedReplayableAndResumable()
    {
        var definition = WorkDefinition.Create("status.assistant", "Streams assistant progress.");
        await using var system = CreateSystem(definition, (context, _, _) =>
        {
            context.Status.Publish("assistant.message.started");
            context.Status.Publish("assistant.text.delta", "Hel");
            context.Status.Publish("assistant.text.delta", "lo");
            context.Status.Publish("assistant.message.completed", new { finishReason = "stop" });
            return Task.FromResult(WorkExecutionResult.Success());
        });
        await system.Start();

        var handle = await system.Queue.Enqueue(definition.Name);
        var completion = await handle.WaitForCompletion();
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected a completed worker.");
        var iteration = new WorkerIterationReference(
            worker.Id,
            worker.LastIterationSequence ?? throw new InvalidOperationException("Expected a completed iteration."));

        var all = await ReadAll(system.IterationStatuses.Subscribe(iteration));
        var resumed = await ReadAll(system.IterationStatuses.Subscribe(iteration, afterSequence: 2));

        Assert.Equal([1L, 2L, 3L, 4L], all.Select(static item => item.Sequence));
        Assert.Equal(
            ["assistant.message.started", "assistant.text.delta", "assistant.text.delta", "assistant.message.completed"],
            all.Select(static item => item.Type));
        Assert.Null(all[0].Data);
        Assert.Equal("Hel", all[1].DeserializeData<string>());
        Assert.Equal("lo", all[2].DeserializeData<string>());
        Assert.Equal("stop", all[3].Data?.GetProperty("finishReason").GetString());
        Assert.All(all, item =>
        {
            Assert.Equal(system.Id, item.WorkSystemId);
            Assert.Equal(iteration, item.Iteration);
            Assert.Equal(definition.Name, item.WorkDefinitionName);
        });
        Assert.Equal([3L, 4L], resumed.Select(static item => item.Sequence));
    }

    [Fact]
    public async Task SlowReaderGetsAnExplicitGapInsteadOfSilentLoss()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            retainedItemCapacity: 2);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.gap");
        await using var subscription = stream.Subscribe(iteration);

        stream.Publish(iteration, "status.gap", WorkIterationStatusUpdate.FromValue("progress", 1));
        stream.Publish(iteration, "status.gap", WorkIterationStatusUpdate.FromValue("progress", 2));
        stream.Publish(iteration, "status.gap", WorkIterationStatusUpdate.FromValue("progress", 3));

        var exception = await Assert.ThrowsAsync<WorkIterationStatusGapException>(async () =>
        {
            await foreach (var _ in subscription.Read())
            {
            }
        });

        Assert.Equal(iteration, exception.Iteration);
        Assert.Equal(0, exception.AfterSequence);
        Assert.Equal(2, exception.FirstAvailableSequence);
        Assert.Equal(3, exception.LastAvailableSequence);
    }

    [Fact]
    public async Task AuthorizedStreamDoesNotRevealUnreadableIterationsOrValidateTheirCursors()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.private");
        stream.Publish(iteration, "status.private", WorkIterationStatusUpdate.FromValue("progress", 1));
        var authorized = new AuthorizedWorkIterationStatusStream(
            stream,
            new HashSet<string>(["status.public"], StringComparer.OrdinalIgnoreCase));

        var items = await ReadAll(authorized.Subscribe(iteration, afterSequence: long.MaxValue));

        Assert.Empty(items);
    }

    [Fact]
    public async Task StreamValidatesConfigurationIdentityAndCursorBoundaries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null, retainedItemCapacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(
                WorkSystemId.New(),
                workSystemName: null,
                maximumPayloadBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(
                WorkSystemId.New(),
                workSystemName: null,
                replayPayloadByteCapacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(
                WorkSystemId.New(),
                workSystemName: null,
                replayPayloadByteCapacity: 5,
                maximumPayloadBytes: 4,
                maximumTypeBytes: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(
                WorkSystemId.New(),
                workSystemName: null,
                maximumTypeBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(
                WorkSystemId.New(),
                workSystemName: null,
                systemReplayItemCapacity: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(
                WorkSystemId.New(),
                workSystemName: null,
                systemReplayByteCapacity: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(
                WorkSystemId.New(),
                workSystemName: null,
                maximumSubscriptionsPerIteration: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusStream(
                WorkSystemId.New(),
                workSystemName: null,
                maximumSubscriptions: 1));

        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Subscribe(iteration, afterSequence: -1));
        Assert.Throws<KeyNotFoundException>(() => stream.Subscribe(iteration));

        stream.Begin(iteration, "status.validation");
        stream.Begin(iteration, "STATUS.VALIDATION");
        Assert.Throws<InvalidOperationException>(() => stream.Begin(iteration, "status.other"));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Subscribe(iteration, afterSequence: 1));

        stream.Complete(iteration);
        Assert.Throws<InvalidOperationException>(() => stream.Publish(
            iteration,
            "status.validation",
            WorkIterationStatusUpdate.FromValue("progress", 1)));
        stream.Complete(iteration);
        stream.Complete(new WorkerIterationReference(WorkerId.New(), 1));
        stream.Forget(new WorkerIterationReference(WorkerId.New(), 1));
    }

    [Fact]
    public async Task PublishCanCreateAStreamBeforeTheIterationBeginNotificationArrives()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), "sample");
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);

        stream.Publish(
            iteration,
            "status.publish-first",
            WorkIterationStatusUpdate.FromValue("assistant.text.delta", "hello"));
        stream.Complete(iteration);

        var item = Assert.Single(await ReadAll(stream.Subscribe(iteration)));
        Assert.Equal("sample", item.WorkSystemName);
        Assert.Equal("hello", item.DeserializeData<string>());
    }

    [Fact]
    public async Task SubscribeRejectsACursorThatPredatesTheRetainedWindow()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            retainedItemCapacity: 2);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.cursor-gap");
        stream.Publish(iteration, "status.cursor-gap", WorkIterationStatusUpdate.FromValue("progress", 1));
        stream.Publish(iteration, "status.cursor-gap", WorkIterationStatusUpdate.FromValue("progress", 2));
        stream.Publish(iteration, "status.cursor-gap", WorkIterationStatusUpdate.FromValue("progress", 3));

        var exception = Assert.Throws<WorkIterationStatusGapException>(() => stream.Subscribe(iteration));

        Assert.Equal(0, exception.AfterSequence);
        Assert.Equal(2, exception.FirstAvailableSequence);
        Assert.Equal(3, exception.LastAvailableSequence);
    }

    [Fact]
    public async Task PublishRejectsOversizedJsonWithoutConsumingASequence()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            maximumPayloadBytes: 6);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.payload-limit");

        var exception = Assert.Throws<WorkIterationStatusPayloadTooLargeException>(() => stream.Publish(
            iteration,
            "status.payload-limit",
            WorkIterationStatusUpdate.FromValue("progress", "hello")));
        stream.Publish(iteration, "status.payload-limit", new WorkIterationStatusUpdate("progress", Data: null));
        stream.Complete(iteration);

        Assert.Equal(7, exception.PayloadBytes);
        Assert.Equal(6, exception.MaximumPayloadBytes);
        Assert.Equal(1, Assert.Single(await ReadAll(stream.Subscribe(iteration))).Sequence);
    }

    [Fact]
    public async Task PublishRejectsOversizedTypeWithoutConsumingASequence()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            maximumTypeBytes: 4);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.type-limit");

        var exception = Assert.Throws<WorkIterationStatusTypeTooLargeException>(() => stream.Publish(
            iteration,
            "status.type-limit",
            new WorkIterationStatusUpdate(" hello ", Data: null)));
        stream.Publish(iteration, "status.type-limit", new WorkIterationStatusUpdate("okay", Data: null));
        stream.Complete(iteration);

        Assert.Equal(5, exception.TypeBytes);
        Assert.Equal(4, exception.MaximumTypeBytes);
        var item = Assert.Single(await ReadAll(stream.Subscribe(iteration)));
        Assert.Equal(1, item.Sequence);
        Assert.Equal("okay", item.Type);
    }

    [Fact]
    public async Task ReplayPayloadByteCapacityEvictsOldestItemsAndReportsAGap()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            retainedItemCapacity: 10,
            replayPayloadByteCapacity: 24,
            maximumPayloadBytes: 8,
            maximumTypeBytes: 8);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.payload-replay");
        stream.Publish(iteration, "status.payload-replay", WorkIterationStatusUpdate.FromValue("progress", "aa"));
        stream.Publish(iteration, "status.payload-replay", WorkIterationStatusUpdate.FromValue("progress", "bb"));
        stream.Publish(iteration, "status.payload-replay", WorkIterationStatusUpdate.FromValue("progress", "cc"));
        stream.Complete(iteration);

        var gap = Assert.Throws<WorkIterationStatusGapException>(() => stream.Subscribe(iteration));
        var retained = await ReadAll(stream.Subscribe(iteration, afterSequence: 1));

        Assert.Equal(2, gap.FirstAvailableSequence);
        Assert.Equal(3, gap.LastAvailableSequence);
        Assert.Equal(["bb", "cc"], retained.Select(item => item.DeserializeData<string>()));
    }

    [Fact]
    public async Task ReplayByteCapacityIncludesStatusTypeBytes()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            retainedItemCapacity: 10,
            replayPayloadByteCapacity: 2,
            maximumPayloadBytes: 1,
            maximumTypeBytes: 1);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.type-accounting");
        stream.Publish(iteration, "status.type-accounting", new WorkIterationStatusUpdate("a", Data: null));
        stream.Publish(iteration, "status.type-accounting", new WorkIterationStatusUpdate("b", Data: null));
        stream.Publish(iteration, "status.type-accounting", new WorkIterationStatusUpdate("c", Data: null));
        stream.Complete(iteration);

        var gap = Assert.Throws<WorkIterationStatusGapException>(() => stream.Subscribe(iteration));
        var retained = await ReadAll(stream.Subscribe(iteration, afterSequence: 1));

        Assert.Equal(2, gap.FirstAvailableSequence);
        Assert.Equal(["b", "c"], retained.Select(static item => item.Type));
    }

    [Fact]
    public async Task SystemReplayItemCapacityEvictsTheOldestItemsAcrossIterations()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            retainedItemCapacity: 2,
            systemReplayItemCapacity: 2);
        var first = new WorkerIterationReference(WorkerId.New(), 1);
        var second = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(first, "status.system-cap");
        stream.Begin(second, "status.system-cap");

        stream.Publish(first, "status.system-cap", new WorkIterationStatusUpdate("first", Data: null));
        stream.Publish(second, "status.system-cap", new WorkIterationStatusUpdate("second", Data: null));
        stream.Publish(first, "status.system-cap", new WorkIterationStatusUpdate("third", Data: null));
        stream.Complete(first);
        stream.Complete(second);

        var gap = Assert.Throws<WorkIterationStatusGapException>(() => stream.Subscribe(first));
        var firstRetained = Assert.Single(await ReadAll(stream.Subscribe(first, afterSequence: 1)));
        var secondRetained = Assert.Single(await ReadAll(stream.Subscribe(second)));

        Assert.Equal(2, gap.FirstAvailableSequence);
        Assert.Equal(2, gap.LastAvailableSequence);
        Assert.Equal("third", firstRetained.Type);
        Assert.Equal("second", secondRetained.Type);
    }

    [Fact]
    public async Task SystemReplayByteCapacityCountsTypesAndPayloadsAcrossIterations()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            retainedItemCapacity: 10,
            replayPayloadByteCapacity: 12,
            systemReplayByteCapacity: 12,
            maximumPayloadBytes: 4,
            maximumTypeBytes: 8);
        var first = new WorkerIterationReference(WorkerId.New(), 1);
        var second = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(first, "status.system-byte-cap");
        stream.Begin(second, "status.system-byte-cap");

        stream.Publish(first, "status.system-byte-cap", WorkIterationStatusUpdate.FromValue("p", "a"));
        stream.Publish(first, "status.system-byte-cap", WorkIterationStatusUpdate.FromValue("p", "b"));
        stream.Publish(second, "status.system-byte-cap", WorkIterationStatusUpdate.FromValue("p", "c"));
        stream.Publish(second, "status.system-byte-cap", WorkIterationStatusUpdate.FromValue("p", "d"));
        stream.Complete(first);
        stream.Complete(second);

        var gap = Assert.Throws<WorkIterationStatusGapException>(() => stream.Subscribe(first));
        var firstRetained = await ReadAll(stream.Subscribe(first, afterSequence: 1));
        var secondRetained = await ReadAll(stream.Subscribe(second));

        Assert.Equal(2, gap.FirstAvailableSequence);
        Assert.Equal("b", Assert.Single(firstRetained).DeserializeData<string>());
        Assert.Equal(["c", "d"], secondRetained.Select(item => item.DeserializeData<string>()));
    }

    [Fact]
    public async Task SystemReplayCapacityReportsAGapWhenAnIterationHasNoRetainedItems()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            retainedItemCapacity: 1,
            systemReplayItemCapacity: 1);
        var first = new WorkerIterationReference(WorkerId.New(), 1);
        var second = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(first, "status.empty-gap");
        stream.Begin(second, "status.empty-gap");
        stream.Publish(first, "status.empty-gap", new WorkIterationStatusUpdate("first", Data: null));
        var liveSubscription = stream.Subscribe(first);
        stream.Publish(second, "status.empty-gap", new WorkIterationStatusUpdate("second", Data: null));
        stream.Complete(first);
        stream.Complete(second);

        var liveGap = await Assert.ThrowsAsync<WorkIterationStatusGapException>(async () =>
        {
            await foreach (var _ in liveSubscription.Read())
            {
            }
        });
        var gap = Assert.Throws<WorkIterationStatusGapException>(() => stream.Subscribe(first));
        var caughtUp = await ReadAll(stream.Subscribe(first, afterSequence: 1));

        Assert.Null(liveGap.FirstAvailableSequence);
        Assert.Null(gap.FirstAvailableSequence);
        Assert.Null(gap.LastAvailableSequence);
        Assert.Empty(caughtUp);
    }

    [Fact]
    public async Task SubscriptionLimitsApplyPerIterationAndAcrossTheSystemAndReleaseOnDispose()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            maximumSubscriptions: 2,
            maximumSubscriptionsPerIteration: 1);
        var first = new WorkerIterationReference(WorkerId.New(), 1);
        var second = new WorkerIterationReference(WorkerId.New(), 1);
        var third = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(first, "status.subscription-limit");
        stream.Begin(second, "status.subscription-limit");
        stream.Begin(third, "status.subscription-limit");
        var firstSubscription = stream.Subscribe(first);
        var perIteration = Assert.Throws<WorkIterationStatusSubscriptionLimitException>(() => stream.Subscribe(first));
        var secondSubscription = stream.Subscribe(second);
        var system = Assert.Throws<WorkIterationStatusSubscriptionLimitException>(() => stream.Subscribe(third));

        Assert.False(perIteration.IsSystemLimit);
        Assert.Equal(1, perIteration.MaximumSubscriptions);
        Assert.True(system.IsSystemLimit);
        Assert.Equal(third, system.Iteration);
        Assert.Equal(2, system.MaximumSubscriptions);

        await firstSubscription.DisposeAsync();
        await using var replacement = stream.Subscribe(third);
        await secondSubscription.DisposeAsync();
    }

    [Fact]
    public async Task IndependentIterationsCanPublishConcurrentlyWithoutLosingSequenceOrder()
    {
        const int iterationCount = 8;
        const int statusCount = 256;
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iterations = Enumerable.Range(0, iterationCount)
            .Select(_ => new WorkerIterationReference(WorkerId.New(), 1))
            .ToArray();
        foreach (var iteration in iterations)
        {
            stream.Begin(iteration, "status.concurrent");
        }

        Parallel.ForEach(iterations, iteration =>
        {
            for (var index = 0; index < statusCount; index++)
            {
                stream.Publish(iteration, "status.concurrent", new WorkIterationStatusUpdate("progress", Data: null));
            }

            stream.Complete(iteration);
        });

        foreach (var iteration in iterations)
        {
            var statuses = await ReadAll(stream.Subscribe(iteration));
            Assert.Equal(statusCount, statuses.Count);
            Assert.Equal(Enumerable.Range(1, statusCount).Select(static value => (long)value),
                statuses.Select(static item => item.Sequence));
        }
    }

    [Fact]
    public async Task ReplayBufferCompactionPreservesSequenceLookup()
    {
        await using var stream = new WorkIterationStatusStream(
            WorkSystemId.New(),
            workSystemName: null,
            retainedItemCapacity: 2);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.compaction");
        for (var sequence = 1; sequence <= 260; sequence++)
        {
            stream.Publish(iteration, "status.compaction", new WorkIterationStatusUpdate("progress", Data: null));
        }

        stream.Complete(iteration);

        var retained = await ReadAll(stream.Subscribe(iteration, afterSequence: 258));

        Assert.Equal([259L, 260L], retained.Select(static item => item.Sequence));
    }

    [Fact]
    public async Task SystemConfigurationAppliesThePayloadLimitToExecutors()
    {
        WorkIterationStatusPayloadTooLargeException? observed = null;
        var definition = WorkDefinition.Create("status.configured-payload", "Uses the system payload limit.");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .ConfigureIterationStatuses(maximumPayloadBytes: 6)
                .AddWork(definition, (context, _, _) =>
                {
                    try
                    {
                        context.Status.Publish("progress", "hello");
                    }
                    catch (WorkIterationStatusPayloadTooLargeException exception)
                    {
                        observed = exception;
                    }

                    return Task.FromResult(WorkExecutionResult.Success());
                }))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue(definition.Name)).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(7, observed?.PayloadBytes);
        Assert.Equal(6, observed?.MaximumPayloadBytes);
    }

    [Fact]
    public async Task SystemConfigurationAppliesTypeAndSubscriptionLimits()
    {
        WorkIterationStatusTypeTooLargeException? observed = null;
        var definition = WorkDefinition.Create("status.configured-security", "Uses status security limits.");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .ConfigureIterationStatuses(
                    maximumTypeBytes: 4,
                    maximumSubscriptions: 1,
                    maximumSubscriptionsPerIteration: 1)
                .AddWork(definition, (context, _, _) =>
                {
                    try
                    {
                        context.Status.Publish("hello");
                    }
                    catch (WorkIterationStatusTypeTooLargeException exception)
                    {
                        observed = exception;
                    }

                    return Task.FromResult(WorkExecutionResult.Success());
                }))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue(definition.Name)).WaitForCompletion();
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected a completed worker.");
        var iteration = new WorkerIterationReference(
            worker.Id,
            worker.LastIterationSequence ?? throw new InvalidOperationException("Expected an iteration."));
        var subscription = system.IterationStatuses.Subscribe(iteration);
        var limit = Assert.Throws<WorkIterationStatusSubscriptionLimitException>(() =>
            system.IterationStatuses.Subscribe(iteration));

        Assert.Equal(5, observed?.TypeBytes);
        Assert.False(limit.IsSystemLimit);
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task ForgettingAWorkerRemovesEveryIterationBufferButNotOtherWorkers()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var workerId = WorkerId.New();
        var first = new WorkerIterationReference(workerId, 1);
        var second = new WorkerIterationReference(workerId, 2);
        var other = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(first, "status.forget-worker");
        stream.Begin(second, "status.forget-worker");
        stream.Begin(other, "status.other-worker");
        stream.Publish(first, "status.forget-worker", WorkIterationStatusUpdate.FromValue("progress", 1));
        stream.Complete(first);
        stream.Complete(second);
        stream.Complete(other);
        var existingSubscription = stream.Subscribe(first);

        stream.Forget(workerId);

        Assert.Single(await ReadAll(existingSubscription));
        Assert.Throws<KeyNotFoundException>(() => stream.Subscribe(first));
        Assert.Throws<KeyNotFoundException>(() => stream.Subscribe(second));
        Assert.Empty(await ReadAll(stream.Subscribe(other)));
        stream.Forget(WorkerId.New());
    }

    [Fact]
    public void ReplayGapValidatesItsAvailableRange()
    {
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        var compatible = new WorkIterationStatusGapException(
            iteration,
            afterSequence: 1,
            firstAvailableSequence: 3);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkIterationStatusGapException(iteration, 0, firstAvailableSequence: 3, lastAvailableSequence: 2));
        var incompleteRange = Assert.Throws<ArgumentException>(() =>
            new WorkIterationStatusGapException(iteration, 0, firstAvailableSequence: 3, lastAvailableSequence: null));

        Assert.Equal(3, compatible.LastAvailableSequence);
        Assert.Equal("lastAvailableSequence", exception.ParamName);
        Assert.Contains("both", incompleteRange.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveReaderWakesForPublishedItemsAndIterationCompletion()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.live");
        await using var subscription = stream.Subscribe(iteration);
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var pendingItem = reader.MoveNextAsync().AsTask();
        Assert.False(pendingItem.IsCompleted);
        stream.Publish(iteration, "status.live", WorkIterationStatusUpdate.FromValue("progress", 1));

        Assert.True(await pendingItem.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, reader.Current.Sequence);

        var pendingCompletion = reader.MoveNextAsync().AsTask();
        Assert.False(pendingCompletion.IsCompleted);
        stream.Complete(iteration);
        Assert.False(await pendingCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancelingAReaderRemovesItsLiveSubscription()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.cancel");
        await using var subscription = stream.Subscribe(iteration);
        using var cancellation = new CancellationTokenSource();
        await using var reader = subscription.Read(cancellation.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task DisposingASubscriptionCompletesItsPendingReader()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.dispose-subscription");
        var subscription = stream.Subscribe(iteration);
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();

        await subscription.DisposeAsync();

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task DisposingTheStreamCompletesReadersAndIsIdempotent()
    {
        var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.dispose-stream");
        await using var subscription = stream.Subscribe(iteration);
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();

        await stream.DisposeAsync();

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await stream.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => stream.Begin(iteration, "status.dispose-stream"));
        Assert.Throws<ObjectDisposedException>(() => stream.Subscribe(iteration));
    }

    [Fact]
    public async Task ForgottenIterationDrainsExistingReaderThenBecomesUnavailable()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.forgotten");
        stream.Publish(iteration, "status.forgotten", WorkIterationStatusUpdate.FromValue("progress", 1));
        var subscription = stream.Subscribe(iteration);

        stream.Forget(iteration);

        Assert.False(stream.TryGetDefinitionName(iteration, out var definitionName));
        Assert.Null(definitionName);
        Assert.Throws<KeyNotFoundException>(() => stream.Subscribe(iteration));
        Assert.Throws<InvalidOperationException>(() => stream.Begin(iteration, "status.forgotten"));
        stream.Forget(iteration);
        var item = Assert.Single(await ReadAll(subscription));
        Assert.Equal(1, item.Sequence);
        Assert.Throws<KeyNotFoundException>(() => stream.Subscribe(iteration));
        stream.Forget(iteration);
    }

    [Fact]
    public async Task SubscriptionCanOnlyBeReadOnce()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.single-reader");
        stream.Complete(iteration);
        await using var subscription = stream.Subscribe(iteration);

        await foreach (var _ in subscription.Read())
        {
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in subscription.Read())
            {
            }
        });
    }

    [Fact]
    public async Task SessionStreamPreservesRequestContextAndDelegatesSubscription()
    {
        await using var stream = new WorkIterationStatusStream(WorkSystemId.New(), workSystemName: null);
        var iteration = new WorkerIterationReference(WorkerId.New(), 1);
        stream.Begin(iteration, "status.session");
        stream.Complete(iteration);
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var sessionStream = new SessionWorkIterationStatusStream(stream, requestContext);

        var items = await ReadAll(sessionStream.Subscribe(iteration));

        Assert.Same(requestContext, sessionStream.RequestContext);
        Assert.Empty(items);
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static async Task<IReadOnlyList<WorkIterationStatusItem>> ReadAll(
        IWorkIterationStatusSubscription subscription)
    {
        await using (subscription)
        {
            var items = new List<WorkIterationStatusItem>();
            await foreach (var item in subscription.Read())
            {
                items.Add(item);
            }

            return items;
        }
    }
}
