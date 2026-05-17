using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Query")]
public sealed class WorkQueryServiceTests
{
    [Fact]
    public async Task WorkerQueryReturnsFullSnapshot()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("snapshot.work", "Can be retrieved."),
            SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("snapshot.work", WorkInput.Empty);
        var worker = await system.Query.Worker(RequiredWorkerId(handle));

        Assert.NotNull(worker);
        Assert.Equal(RequiredWorkerId(handle), worker.Id);
        Assert.Equal("snapshot.work", worker.DefinitionName);
    }

    [Fact]
    public async Task WorkersQueryReturnsOverviewItemsFilteredByDefinitionSubjectConcurrencyKeyAndIdentifier()
    {
        var subject = new WorkSubjectId("customer", "123");
        var key = new WorkConcurrencyKey("tenant", "tenant-a");
        var identifier = new WorkIdentifier("invoice", "inv-100");
        var definition = WorkDefinition.Create("invoice.sync", "Synchronizes invoices.",
            category: "Finance:Invoices");
        await using var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var accepted = await system.Queue.Enqueue(
            "invoice.sync",
            WorkInput.Empty
                .WithSubject(subject)
                .WithConcurrencyKey(key)
                .WithIdentifier(identifier));
        await system.Queue.Enqueue("invoice.sync", WorkInput.Empty.WithIdentifier(new WorkIdentifier("invoice", "inv-200")));

        var result = await system.Query.Workers(new WorkerCriteria(
            DefinitionId: definition.Id,
            SubjectId: subject,
            ConcurrencyKey: key,
            Identifier: identifier));

        var onlyWorker = Assert.Single(result.Workers);
        Assert.Equal(RequiredWorkerId(accepted), onlyWorker.Id);
        Assert.Equal("invoice.sync", onlyWorker.DefinitionName);
        Assert.Equal(subject, onlyWorker.SubjectId);
        Assert.Equal(key, onlyWorker.ConcurrencyKey);
        Assert.Contains(identifier, onlyWorker.Identifiers);
        Assert.Equal("Finance:Invoices", onlyWorker.Category);
    }

    [Fact]
    public async Task WorkersQueryCanFindIdentifiersDiscoveredDuringExecution()
    {
        var discovered = new WorkIdentifier("order", "ord-123");
        await using var system = CreateSystem(
            WorkDefinition.Create("discover.relationships", "Adds identifiers while running."),
            (context, _, _) =>
            {
                Assert.True(context.AddIdentifier(discovered));
                Assert.False(context.AddIdentifier(discovered));
                return Task.FromResult(WorkExecutionResult.Success());
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("discover.relationships", WorkInput.Empty);
        await handle.WaitForCompletion();

        var result = await system.Query.Workers(new WorkerCriteria(Identifier: discovered));

        var onlyWorker = Assert.Single(result.Workers);
        Assert.Equal(RequiredWorkerId(handle), onlyWorker.Id);
        Assert.Contains(discovered, onlyWorker.Identifiers);
    }

    [Fact]
    public async Task WorkerSnapshotExposesCurrentAndLastIterationSequences()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var system = CreateSystem(
            WorkDefinition.Create("iteration.sequence", "Exposes current iteration sequence."),
            async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success(WorkOutput.FromValue(new { ok = true }));
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("iteration.sequence", WorkInput.Empty);
        var workerId = RequiredWorkerId(handle);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var running = await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected running worker.");

        release.TrySetResult();
        await handle.WaitForCompletion();
        var completed = await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected completed worker.");

        Assert.Equal(1, running.CurrentIterationSequence);
        Assert.Null(running.LastIterationSequence);
        Assert.Null(completed.CurrentIterationSequence);
        Assert.Equal(1, completed.LastIterationSequence);
        Assert.Equal(1, completed.LastIteration?.Sequence);
    }

    [Fact]
    public async Task WorkerIterationsQueryCanFilterExecutingIteration()
    {
        var discoveredIdentifier = new WorkIdentifier("execution", "active");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var system = CreateSystem(
            WorkDefinition.Create("iteration.executing", "Keeps an iteration executing."),
            async (context, _, cancellationToken) =>
            {
                context.AddIdentifier(discoveredIdentifier);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("iteration.executing", WorkInput.Empty);
        var workerId = RequiredWorkerId(handle);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var snapshot = await system.Query.WorkerIteration(new WorkerIterationReference(workerId, 1));
            var executing = await system.Query.WorkerIterations(new WorkerIterationCriteria(
                Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Executing }));
            var executingByDiscoveredIdentifier = await system.Query.WorkerIterations(new WorkerIterationCriteria(
                Identifier: discoveredIdentifier,
                Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Executing }));
            var overview = await system.Query.SystemDetails();

            Assert.NotNull(snapshot);
            Assert.Equal(WorkCompletionStatus.Executing, snapshot.Status);
            var item = Assert.Single(executing.Iterations);
            Assert.Equal(workerId, item.WorkerId);
            Assert.Equal(WorkCompletionStatus.Executing, item.Status);
            var itemByDiscoveredIdentifier = Assert.Single(executingByDiscoveredIdentifier.Iterations);
            Assert.Equal(workerId, itemByDiscoveredIdentifier.WorkerId);
            Assert.Contains(discoveredIdentifier, itemByDiscoveredIdentifier.Identifiers);
            Assert.Equal(1, overview.CurrentIterationCount);
            Assert.Equal(1, overview.IterationCountByStatus[WorkCompletionStatus.Executing]);
        }
        finally
        {
            release.TrySetResult();
            await handle.WaitForCompletion();
        }
    }

    [Fact]
    public async Task WorkerIterationsQueryReturnsFullSnapshotsAndOverviewItems()
    {
        var subject = new WorkSubjectId("claim", "CLM-123");
        var concurrencyKey = new WorkConcurrencyKey("tenant", "west");
        var queuedIdentifier = new WorkIdentifier("invoice", "INV-456");
        var discoveredIdentifier = new WorkIdentifier("claim-note", "CLM-123-note");
        var definition = WorkDefinition.Create("iteration.query", "Can query iterations.", category: "Claims");
        await using var system = CreateSystem(
            definition,
            (context, input, cancellationToken) =>
            {
                context.AddIdentifier(discoveredIdentifier);
                return Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromValue(new { processed = true })));
            });

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "iteration.query",
            WorkInput.Empty
                .WithSubject(subject)
                .WithConcurrencyKey(concurrencyKey)
                .WithIdentifier(queuedIdentifier));
        await handle.WaitForCompletion();
        var workerId = RequiredWorkerId(handle);
        var snapshot = await system.Query.WorkerIteration(new WorkerIterationReference(workerId, 1));
        var bySubject = await system.Query.WorkerIterations(new WorkerIterationCriteria(SubjectId: subject));
        var byConcurrencyKey = await system.Query.WorkerIterations(new WorkerIterationCriteria(ConcurrencyKey: concurrencyKey));
        var byQueuedIdentifier = await system.Query.WorkerIterations(new WorkerIterationCriteria(Identifier: queuedIdentifier));
        var byDiscoveredIdentifier = await system.Query.WorkerIterations(new WorkerIterationCriteria(Identifier: discoveredIdentifier));
        var byDefinitionNameAndStatus = await system.Query.WorkerIterations(new WorkerIterationCriteria(
            DefinitionName: "iteration.query",
            Category: "Claims",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed }));

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.Sequence);
        Assert.Equal(WorkCompletionStatus.Completed, snapshot.Status);
        Assert.Contains("processed", snapshot.Output?.Json);
        var item = Assert.Single(byDefinitionNameAndStatus.Iterations);
        Assert.Equal(workerId, item.WorkerId);
        Assert.Equal(definition.Id, item.DefinitionId);
        Assert.Equal("iteration.query", item.DefinitionName);
        Assert.Equal("Claims", item.Category);
        Assert.Equal(WorkerState.Completed, item.WorkerState);
        Assert.Equal(WorkCompletionStatus.Completed, item.Status);
        Assert.Equal(subject, item.SubjectId);
        Assert.Equal(concurrencyKey, item.ConcurrencyKey);
        Assert.Contains(queuedIdentifier, item.Identifiers);
        Assert.Contains(discoveredIdentifier, item.Identifiers);
        Assert.Equal(workerId, Assert.Single(bySubject.Iterations).WorkerId);
        Assert.Equal(workerId, Assert.Single(byConcurrencyKey.Iterations).WorkerId);
        Assert.Equal(workerId, Assert.Single(byQueuedIdentifier.Iterations).WorkerId);
        Assert.Equal(workerId, Assert.Single(byDiscoveredIdentifier.Iterations).WorkerId);
    }

    [Fact]
    public async Task WorkerIterationsQueryCanFindTransientRetryAttempts()
    {
        var attempts = 0;
        var definition = WorkDefinition.Create("iteration.retry", "Retries transient failures.");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (context, input, cancellationToken) =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        throw new TimeoutException("Try again.");
                    }

                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration
                    .RetryTransientFailures(
                        count: 1,
                        initialDelay: TimeSpan.FromMilliseconds(1),
                        jitter: TimeSpan.Zero)
                    .ClassifyExceptions(exception => exception is TimeoutException
                        ? WorkExceptionClassification.Transient
                        : WorkExceptionClassification.Unknown)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue("iteration.retry");
        await handle.WaitForCompletion();
        var workerId = RequiredWorkerId(handle);
        var failed = await system.Query.WorkerIterations(new WorkerIterationCriteria(
            WorkerId: workerId,
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Failed }));
        var completed = await system.Query.WorkerIterations(new WorkerIterationCriteria(
            WorkerId: workerId,
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed }));
        var worker = await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal(2, attempts);
        Assert.Equal(2, worker.LastIterationSequence);
        Assert.Equal(1, Assert.Single(failed.Iterations).Sequence);
        Assert.Equal(2, Assert.Single(completed.Iterations).Sequence);
        Assert.Equal([WorkCompletionStatus.Failed, WorkCompletionStatus.Completed], worker.Iterations.Select(iteration => iteration.Status));
    }

    [Fact]
    public async Task PurgingWorkerRemovesIndexedIterations()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("iteration.purge", "Purges iterations with the worker."),
            SuccessfulWork);
        await system.Start();

        var handle = await system.Queue.Enqueue("iteration.purge");
        await handle.WaitForCompletion();
        var workerId = RequiredWorkerId(handle);
        var beforePurge = await system.Query.WorkerIterations(new WorkerIterationCriteria(WorkerId: workerId));
        var worker = await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");

        await system.Workers.Execute(worker.Version, WorkAction.Purge);
        var afterPurge = await system.Query.WorkerIterations(new WorkerIterationCriteria(WorkerId: workerId));

        Assert.Single(beforePurge.Iterations);
        Assert.Empty(afterPurge.Iterations);
        Assert.Null(await system.Query.WorkerIteration(new WorkerIterationReference(workerId, 1)));
    }

    [Fact]
    public async Task WorkerKeysQuerySearchesSubjectsConcurrencyKeysAndIdentifiers()
    {
        var subject = new WorkSubjectId("claim", "CLM-123");
        var concurrencyKey = new WorkConcurrencyKey("tenant", "west");
        var queuedIdentifier = new WorkIdentifier("invoice", "INV-456");
        var discoveredIdentifier = new WorkIdentifier("claim-note", "CLM-123-note");
        await using var system = CreateSystem(
            WorkDefinition.Create("keyed.work", "Adds searchable keys."),
            (context, input, cancellationToken) =>
            {
                context.AddIdentifier(discoveredIdentifier);
                return Task.FromResult(WorkExecutionResult.Success());
            });

        await system.Start();

        var first = await system.Queue.Enqueue(
            "keyed.work",
            WorkInput.Empty
                .WithSubject(subject)
                .WithConcurrencyKey(concurrencyKey)
                .WithIdentifier(queuedIdentifier));
        await first.WaitForCompletion();
        var second = await system.Queue.Enqueue(
            "keyed.work",
            WorkInput.Empty.WithSubject(subject));
        await second.WaitForCompletion();

        var claimKeys = await system.Query.WorkerKeys(new WorkerKeyCriteria(Search: "claim id CLM-123"));
        var subjectKeys = await system.Query.WorkerKeys(new WorkerKeyCriteria(Kind: WorkKeyKind.Subject, Type: "claim"));
        var types = await system.Query.WorkerKeyTypes(new WorkerKeyTypeCriteria(Search: "claim work"));

        Assert.Contains(claimKeys.Keys, key =>
            key.Kind == WorkKeyKind.Subject &&
            key.Type == "claim" &&
            key.Value == "CLM-123" &&
            key.Workers.Select(worker => worker.Id).ToHashSet().SetEquals([RequiredWorkerId(first), RequiredWorkerId(second)]));
        Assert.Contains(claimKeys.Keys, key => key.Kind == WorkKeyKind.Identifier && key.Type == "claim-note" && key.Value == "CLM-123-note");
        var subjectKey = Assert.Single(subjectKeys.Keys);
        Assert.Equal("CLM-123", subjectKey.Value);
        Assert.Contains(types.Types, type =>
            type.Type == "claim" &&
            type.WorkerCount == 2 &&
            type.WorkerCountByKind[WorkKeyKind.Subject] == 2 &&
            type.Workers.Select(worker => worker.Id).ToHashSet().SetEquals([RequiredWorkerId(first), RequiredWorkerId(second)]));
        Assert.Contains(types.Types, type =>
            type.Type == "claim-note" &&
            type.WorkerCount == 2 &&
            type.WorkerCountByKind[WorkKeyKind.Identifier] == 2);
    }

    [Fact]
    public async Task WorkerKeyTypesQueryGroupsByTypeAcrossAllKeyKinds()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("key.type.grouping", "Adds keys with shared types."),
            SuccessfulWork);
        await system.Start();

        var first = await system.Queue.Enqueue(
            "key.type.grouping",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-1"))
                .WithConcurrencyKey(new WorkConcurrencyKey("claim", "CLM-1"))
                .WithIdentifier(new WorkIdentifier("claim", "CLM-1")));
        await first.WaitForCompletion();
        var second = await system.Queue.Enqueue(
            "key.type.grouping",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("claim", "CLM-2")));
        await second.WaitForCompletion();

        var types = await system.Query.WorkerKeyTypes(new WorkerKeyTypeCriteria(Type: "claim"));
        var pagedTypes = await system.Query.WorkerKeyTypes(new WorkerKeyTypeCriteria(Take: 1));
        var keys = await system.Query.WorkerKeys(new WorkerKeyCriteria(Type: "claim"));

        var type = Assert.Single(types.Types);
        Assert.Equal("claim", type.Type);
        Assert.Equal(2, type.WorkerCount);
        Assert.Equal(1, type.WorkerCountByKind[WorkKeyKind.Subject]);
        Assert.Equal(1, type.WorkerCountByKind[WorkKeyKind.ConcurrencyKey]);
        Assert.Equal(2, type.WorkerCountByKind[WorkKeyKind.Identifier]);
        Assert.Equal(2, type.Workers.Count);
        Assert.Equal(4, keys.Keys.Count);
        Assert.Single(pagedTypes.Types);
        Assert.Equal(1, pagedTypes.Take);
        Assert.Equal(1, pagedTypes.TotalCount);
    }

    [Fact]
    public async Task WorkerKeysQueryCapsOversizedTakeAtSafeMaximum()
    {
        var definition = WorkDefinition.Create(
            "key.take.cap",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        await using var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        for (var index = 0; index < WorkerKeyCriteria.MaximumTake + 5; index++)
        {
            await system.Queue.Enqueue(
                "key.take.cap",
                WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", index.ToString())));
        }

        var result = await system.Query.WorkerKeys(new WorkerKeyCriteria(Take: WorkerKeyCriteria.MaximumTake + 5));

        Assert.Equal(WorkerKeyCriteria.MaximumTake, result.Take);
        Assert.Equal(WorkerKeyCriteria.MaximumTake, result.Keys.Count);
        Assert.Equal(WorkerKeyCriteria.MaximumTake + 5, result.TotalCount);
    }

    [Fact]
    public async Task WorkerKeysQueryCanFilterResolvedWorkersByState()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var system = CreateSystem(
            WorkDefinition.Create("key.running", "Keeps a keyed worker running."),
            async (context, input, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            });

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "key.running",
            WorkInput.Empty.WithSubject(new WorkSubjectId("claim", "CLM-777")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var running = await system.Query.WorkerKeys(new WorkerKeyCriteria(
                Search: "claim id CLM-777",
                States: new HashSet<WorkerState> { WorkerState.Running }));
            var completed = await system.Query.WorkerKeys(new WorkerKeyCriteria(
                Search: "claim id CLM-777",
                States: new HashSet<WorkerState> { WorkerState.Completed }));

            var key = Assert.Single(running.Keys);
            var worker = Assert.Single(key.Workers);
            Assert.Equal(RequiredWorkerId(handle), worker.Id);
            Assert.Equal(WorkerState.Running, worker.State);
            Assert.Empty(completed.Keys);
        }
        finally
        {
            release.TrySetResult();
            await handle.WaitForCompletion();
        }
    }

    [Fact]
    public async Task WorkIterationKeysQuerySearchesSubjectsConcurrencyKeysAndIdentifiers()
    {
        var attempts = 0;
        var subject = new WorkSubjectId("claim", "CLM-123");
        var concurrencyKey = new WorkConcurrencyKey("tenant", "west");
        var queuedIdentifier = new WorkIdentifier("invoice", "INV-456");
        var discoveredIdentifier = new WorkIdentifier("claim-note", "CLM-123-note");
        var definition = WorkDefinition.Create("iteration.keyed.work", "Adds searchable iteration keys.");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (context, input, cancellationToken) =>
                {
                    context.AddIdentifier(discoveredIdentifier);
                    attempts++;
                    if (attempts == 1)
                    {
                        throw new TimeoutException("Try again.");
                    }

                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration
                    .RetryTransientFailures(
                        count: 1,
                        initialDelay: TimeSpan.FromMilliseconds(1),
                        jitter: TimeSpan.Zero)
                    .ClassifyExceptions(exception => exception is TimeoutException
                        ? WorkExceptionClassification.Transient
                        : WorkExceptionClassification.Unknown)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var handle = await system.Queue.Enqueue(
            "iteration.keyed.work",
            WorkInput.Empty
                .WithSubject(subject)
                .WithConcurrencyKey(concurrencyKey)
                .WithIdentifier(queuedIdentifier));
        await handle.WaitForCompletion();

        var claimKeys = await system.Query.WorkIterationKeys(new WorkIterationKeyCriteria(Search: "claim id CLM-123"));
        var subjectKeys = await system.Query.WorkIterationKeys(new WorkIterationKeyCriteria(Kind: WorkKeyKind.Subject, Type: "claim"));
        var completedKeys = await system.Query.WorkIterationKeys(new WorkIterationKeyCriteria(
            Kind: WorkKeyKind.Subject,
            Type: "claim",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed }));

        Assert.Contains(claimKeys.Keys, key =>
            key.Kind == WorkKeyKind.Subject &&
            key.Type == "claim" &&
            key.Value == "CLM-123" &&
            key.Iterations.Count == 2 &&
            key.Iterations.Select(iteration => iteration.Status).ToHashSet().SetEquals(
                [WorkCompletionStatus.Completed, WorkCompletionStatus.Failed]));
        Assert.Contains(claimKeys.Keys, key => key.Kind == WorkKeyKind.Identifier && key.Type == "claim-note" && key.Value == "CLM-123-note");
        var subjectKey = Assert.Single(subjectKeys.Keys);
        Assert.Equal("CLM-123", subjectKey.Value);
        Assert.Equal(2, subjectKey.Iterations.Count);
        var completedSubjectKey = Assert.Single(completedKeys.Keys);
        var completedIteration = Assert.Single(completedSubjectKey.Iterations);
        Assert.Equal(WorkCompletionStatus.Completed, completedIteration.Status);
    }

    [Fact]
    public async Task WorkIterationKeyTypesQueryGroupsByTypeAcrossAllKeyKinds()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("iteration.key.type.grouping", "Adds iteration keys with shared types."),
            SuccessfulWork);
        await system.Start();

        await (await system.Queue.Enqueue(
            "iteration.key.type.grouping",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-1"))
                .WithConcurrencyKey(new WorkConcurrencyKey("claim", "CLM-1"))
                .WithIdentifier(new WorkIdentifier("claim", "CLM-1")))).WaitForCompletion();
        await (await system.Queue.Enqueue(
            "iteration.key.type.grouping",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("claim", "CLM-2")))).WaitForCompletion();

        var types = await system.Query.WorkIterationKeyTypes(new WorkIterationKeyTypeCriteria(Type: "claim"));
        var pagedTypes = await system.Query.WorkIterationKeyTypes(new WorkIterationKeyTypeCriteria(Take: 1));
        var keys = await system.Query.WorkIterationKeys(new WorkIterationKeyCriteria(Type: "claim"));

        var type = Assert.Single(types.Types);
        Assert.Equal("claim", type.Type);
        Assert.Equal(2, type.IterationCount);
        Assert.Equal(1, type.IterationCountByKind[WorkKeyKind.Subject]);
        Assert.Equal(1, type.IterationCountByKind[WorkKeyKind.ConcurrencyKey]);
        Assert.Equal(2, type.IterationCountByKind[WorkKeyKind.Identifier]);
        Assert.Equal(2, type.Iterations.Count);
        Assert.Equal(4, keys.Keys.Count);
        Assert.Single(pagedTypes.Types);
        Assert.Equal(1, pagedTypes.Take);
        Assert.Equal(1, pagedTypes.TotalCount);
    }

    [Fact]
    public async Task WorkIterationKeysQueryCapsOversizedTakeAtSafeMaximum()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("iteration.key.take.cap", "Adds many iteration keys."),
            SuccessfulWork);

        await system.Start();

        for (var index = 0; index < WorkIterationKeyCriteria.MaximumTake + 5; index++)
        {
            await (await system.Queue.Enqueue(
                "iteration.key.take.cap",
                WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", index.ToString())))).WaitForCompletion();
        }

        var result = await system.Query.WorkIterationKeys(new WorkIterationKeyCriteria(Take: WorkIterationKeyCriteria.MaximumTake + 5));

        Assert.Equal(WorkIterationKeyCriteria.MaximumTake, result.Take);
        Assert.Equal(WorkIterationKeyCriteria.MaximumTake, result.Keys.Count);
        Assert.Equal(WorkIterationKeyCriteria.MaximumTake + 5, result.TotalCount);
    }

    [Fact]
    public async Task WorkersQueryCanFilterByWorkerState()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var system = CreateSystem(
            WorkDefinition.Create("long.running", "Waits until the test releases it."),
            async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            });

        await system.Start();

        var handle = await system.Queue.Enqueue("long.running", WorkInput.Empty);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var running = await system.Query.Workers(new WorkerCriteria(
            States: new HashSet<WorkerState> { WorkerState.Running }));

        release.TrySetResult();
        await handle.WaitForCompletion();
        var completed = await system.Query.Workers(new WorkerCriteria(
            States: new HashSet<WorkerState> { WorkerState.Completed }));

        var onlyRunningWorker = Assert.Single(running.Workers);
        Assert.Equal(RequiredWorkerId(handle), onlyRunningWorker.Id);
        Assert.Equal(WorkerState.Running, onlyRunningWorker.State);
        Assert.Equal(RequiredWorkerId(handle), Assert.Single(completed.Workers).Id);
    }

    [Fact]
    public async Task WorkersQueryCanFilterByConfiguration()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create(
                        "query.config.recurrence",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                            Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(5)),
                        }),
                    SuccessfulWork);
                builder.AddWork(
                    WorkDefinition.Create(
                        "query.config.concurrency",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                            Concurrency = WorkConcurrencyConfiguration.Default with
                            {
                                IsEnabled = true,
                                MaximumCapacity = 1,
                            },
                        }),
                    SuccessfulWork);
                builder.AddWork(
                    WorkDefinition.Create(
                        "query.config.profiling",
                        defaultOptions: new WorkerOptions(
                            ProfilingEnabled: true,
                            Configuration: WorkConfiguration.Default with
                            {
                                Start = WorkStartConfiguration.DoNotStart,
                            })),
                    SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var recurrence = await system.Queue.Enqueue("query.config.recurrence");
        var concurrency = await system.Queue.Enqueue("query.config.concurrency");
        var profiling = await system.Queue.Enqueue("query.config.profiling");

        var recurringWorkers = await system.Query.Workers(new WorkerCriteria(
            Configuration: new WorkerConfigurationCriteria(RecurrenceEnabled: true)));
        var concurrencyWorkers = await system.Query.Workers(new WorkerCriteria(
            Configuration: new WorkerConfigurationCriteria(ConcurrencyEnabled: true)));
        var profilingWorkers = await system.Query.Workers(new WorkerCriteria(
            Configuration: new WorkerConfigurationCriteria(ProfilingEnabled: true)));
        var nonProfilingWorkers = await system.Query.Workers(new WorkerCriteria(
            Configuration: new WorkerConfigurationCriteria(ProfilingEnabled: false)));

        Assert.Equal(RequiredWorkerId(recurrence), Assert.Single(recurringWorkers.Workers).Id);
        Assert.Equal(RequiredWorkerId(concurrency), Assert.Single(concurrencyWorkers.Workers).Id);
        Assert.Equal(RequiredWorkerId(profiling), Assert.Single(profilingWorkers.Workers).Id);
        Assert.DoesNotContain(nonProfilingWorkers.Workers, worker => worker.Id == RequiredWorkerId(profiling));
        Assert.Equal(2, nonProfilingWorkers.TotalCount);
    }

    [Fact]
    public async Task WorkersQueryConfigurationIndexUpdatesAfterReconfiguration()
    {
        var definition = WorkDefinition.Create(
            "query.config.reconfigure",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var handle = await system.Queue.Enqueue("query.config.reconfigure");
        var worker = await system.Query.Worker(RequiredWorkerId(handle))
            ?? throw new InvalidOperationException("Expected worker.");
        var before = await system.Query.Workers(new WorkerCriteria(
            Configuration: new WorkerConfigurationCriteria(
                RecurrenceEnabled: false,
                ConcurrencyEnabled: false,
                ProfilingEnabled: false)));

        var outcome = await system.Workers.Reconfigure(
            worker.Version,
            new WorkerReconfiguration(
                ProfilingEnabled: true,
                Recurrence: WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
                Concurrency: WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = 1,
                }));
        var afterEnabled = await system.Query.Workers(new WorkerCriteria(
            Configuration: new WorkerConfigurationCriteria(
                RecurrenceEnabled: true,
                ConcurrencyEnabled: true,
                ProfilingEnabled: true)));
        var afterDisabled = await system.Query.Workers(new WorkerCriteria(
            Configuration: new WorkerConfigurationCriteria(
                RecurrenceEnabled: false,
                ConcurrencyEnabled: false,
                ProfilingEnabled: false)));

        Assert.True(outcome.IsAccepted);
        Assert.Equal(RequiredWorkerId(handle), Assert.Single(before.Workers).Id);
        Assert.Equal(RequiredWorkerId(handle), Assert.Single(afterEnabled.Workers).Id);
        Assert.Empty(afterDisabled.Workers);
    }

    [Fact]
    public async Task WorkersQueryCapsOversizedTakeAtSafeMaximum()
    {
        var definition = WorkDefinition.Create(
            "query.take.cap",
            "Caps oversized query pages.",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        await using var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        for (var index = 0; index < WorkerCriteria.MaximumTake + 5; index++)
        {
            await system.Queue.Enqueue("query.take.cap", WorkInput.Empty);
        }

        var result = await system.Query.Workers(new WorkerCriteria(Take: WorkerCriteria.MaximumTake + 5));

        Assert.Equal(WorkerCriteria.MaximumTake, result.Take);
        Assert.Equal(WorkerCriteria.MaximumTake, result.Workers.Count);
        Assert.Equal(WorkerCriteria.MaximumTake + 5, result.TotalCount);
    }

    [Fact]
    public async Task WorkInfoQueryReturnsDefinitionStatusAndWorkerRollup()
    {
        var definition = WorkDefinition.Create("rollup.work", "Reports worker counts.",
            category: "Operations:Rollups");
        await using var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        await system.Queue.Enqueue("rollup.work", WorkInput.Empty);
        var info = await system.Query.WorkInfo("rollup.work");

        Assert.NotNull(info);
        Assert.Equal(definition.Id, info.Definition.Id);
        Assert.Equal("Operations:Rollups", info.Definition.Category);
        Assert.Equal(1, info.Workers.Total);
        Assert.True(info.Status is WorkDefinitionStatus.Healthy or WorkDefinitionStatus.Inactive);
    }

    [Fact]
    public async Task WorkerSummariesDoNotCountFailedWorkersAsActive()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("summary.failed"),
                    (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("summary.failed", "Failed.")])));
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        await (await system.Queue.Enqueue("summary.failed")).WaitForCompletion();

        var summary = await system.Query.WorkerStatusSummary();
        var info = await system.Query.WorkInfo("summary.failed")
            ?? throw new InvalidOperationException("Expected work info.");

        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.Active);
        Assert.Equal(0, summary.Final);
        Assert.Equal(1, summary.Counts[WorkerState.Failed]);
        Assert.Equal(1, info.Workers.Total);
        Assert.Equal(0, info.Workers.Active);
        Assert.Equal(1, info.Workers.Failed);
    }

    [Fact]
    public async Task WorkDefinitionsQueryFiltersByCategoryAndSearch()
    {
        var billing = WorkDefinition.Create("invoice.send", "Sends invoice email.",
            category: "Finance:Invoices");
        var cache = WorkDefinition.Create("cache.refresh", "Refreshes cached values.",
            category: "Operations:Cache");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(billing, SuccessfulWork);
                builder.AddWork(cache, SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var definitions = await system.Query.WorkDefinitions(new WorkDefinitionCriteria(
            Category: "Finance",
            Search: "invoice"));

        var onlyDefinition = Assert.Single(definitions);
        Assert.Equal("invoice.send", onlyDefinition.Name);
    }

    [Fact]
    public async Task WorkDefinitionsQueryUsesCategoryPathWithoutMatchingSimilarNames()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(WorkDefinition.Create("finance.root", category: "Finance"), SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("invoice.send", category: "Finance:Invoices"), SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("finance.operations", category: "FinanceOperations"), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var definitions = await system.Query.WorkDefinitions(new WorkDefinitionCriteria(Category: "finance"));

        Assert.Equal(["finance.root", "invoice.send"], definitions.Select(definition => definition.Name));
    }

    [Fact]
    public async Task WorkersQueryCanFilterByCategoryPath()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(WorkDefinition.Create("finance.root", category: "Finance"), SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("invoice.send", category: "Finance:Invoices"), SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("operations.cache", category: "Operations:Cache"), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        await (await system.Queue.Enqueue("finance.root")).WaitForCompletion();
        await (await system.Queue.Enqueue("invoice.send")).WaitForCompletion();
        await (await system.Queue.Enqueue("operations.cache")).WaitForCompletion();

        var finance = await system.Query.Workers(new WorkerCriteria(Category: "finance"));
        var exactFinance = await system.Query.Workers(new WorkerCriteria(
            Category: "finance",
            IncludeSubcategories: false));

        Assert.Equal(["finance.root", "invoice.send"], finance.Workers.Select(worker => worker.DefinitionName).OrderBy(name => name));
        var worker = Assert.Single(exactFinance.Workers);
        Assert.Equal("finance.root", worker.DefinitionName);
    }

    [Fact]
    public async Task WorkMetadataAttributeSuppliesBrowsableNameCategoryAndDescription()
    {
        var definition = WorkDefinition.Create("placeholder", "Placeholder.");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<AttributedMetadataWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var onlyDefinition = Assert.Single(await system.Query.WorkDefinitions(new WorkDefinitionCriteria(Category: "People:Onboarding")));

        Assert.Equal("employee.onboard", onlyDefinition.Name);
        Assert.Equal("People:Onboarding", onlyDefinition.Category);
        Assert.Equal("Creates onboarding tasks for a new employee.", onlyDefinition.Description);
    }

    [Fact]
    public async Task WorkerStatusSummaryQueryReturnsCounts()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("status.work", "Summarizes status."),
            SuccessfulWork);

        await system.Start();

        await system.Queue.Enqueue("status.work", WorkInput.Empty);

        var summary = await system.Query.WorkerStatusSummary();

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Counts.Values.Sum());
    }

    [Fact]
    public async Task SystemDetailsQueryReturnsCountsAndSlimFailedAndCompletedIterations()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem("overview", builder =>
            {
                builder.AddWork(WorkDefinition.Create("overview.complete", category: "Overview"), SuccessfulWork);
                builder.AddWork(
                    WorkDefinition.Create("overview.failed", category: "Overview"),
                    (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("overview.failed", "Failed.")])));
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var completed = await system.Queue.Enqueue("overview.complete", WorkInput.Empty);
        await completed.WaitForCompletion();
        var failed = await system.Queue.Enqueue("overview.failed", WorkInput.Empty);
        await failed.WaitForCompletion();

        var overview = await system.Query.SystemDetails();

        Assert.Equal("overview", overview.SystemName);
        Assert.Equal(WorkSystemState.Started, overview.SystemState);
        Assert.Equal(0, overview.DefinitionCount);
        Assert.Equal(0, overview.ActiveWorkerCount);
        Assert.Equal(1, overview.FinalWorkerCount);
        Assert.Equal(1, overview.FailedWorkerCount);
        Assert.Equal(1, overview.WorkerCountByState[WorkerState.Completed]);
        Assert.Equal(1, overview.WorkerCountByState[WorkerState.Failed]);
        Assert.Equal(0, overview.CurrentIterationCount);
        Assert.Equal(1, overview.CompletedIterationCount);
        Assert.Equal(1, overview.FailedIterationCount);
        Assert.Equal(1, overview.IterationCountByStatus[WorkCompletionStatus.Completed]);
        Assert.Equal(1, overview.IterationCountByStatus[WorkCompletionStatus.Failed]);
        Assert.Empty(overview.CommonKeyTypes);
        var failedWorker = Assert.Single(overview.FailedWorkers);
        Assert.Equal(RequiredWorkerId(failed), failedWorker.Id);
        Assert.Equal("overview.failed", failedWorker.DefinitionName);
        Assert.Equal("Overview", failedWorker.Category);
        Assert.Equal(WorkerState.Failed, failedWorker.State);
        Assert.Equal(RequiredWorkerId(failed), Assert.Single(overview.FailedIterations).WorkerId);
        var completedItem = Assert.Single(overview.CompletedIterations);
        Assert.Equal(RequiredWorkerId(completed), completedItem.WorkerId);
        Assert.Equal(1, completedItem.Sequence);
        Assert.Equal("overview.complete", completedItem.DefinitionName);
        Assert.Equal("Overview", completedItem.Category);
    }

    [Fact]
    public async Task SystemDetailsQueryCountsDefinitionsWithActiveOrQueuedWorkersFromIndex()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var system = new ServiceCollection()
            .AddWorkableSystem("overview-active-definitions", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("overview.active", category: "Overview"),
                    async (_, _, cancellationToken) =>
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWork(
                    WorkDefinition.Create(
                        "overview.queued",
                        category: "Overview",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                        }),
                    SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("overview.completed", category: "Overview"), SuccessfulWork);
                builder.AddWork(
                    WorkDefinition.Create("overview.not-counted-failed", category: "Overview"),
                    (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("overview.failed", "Failed.")])));
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var active = await system.Queue.Enqueue("overview.active");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await system.Queue.Enqueue("overview.queued");
        await (await system.Queue.Enqueue("overview.completed")).WaitForCompletion();
        await (await system.Queue.Enqueue("overview.not-counted-failed")).WaitForCompletion();

        try
        {
            var details = await system.Query.SystemDetails();

            Assert.Equal(2, details.DefinitionCount);
            Assert.Equal(1, details.CurrentIterationCount);
            Assert.Equal(1, details.IterationCountByStatus[WorkCompletionStatus.Executing]);
        }
        finally
        {
            release.TrySetResult();
            await active.WaitForCompletion();
        }
    }

    [Fact]
    public async Task SystemDetailsQueryLimitsCompletedAndFailedIterationsToFiveRecentItems()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem("overview-limit", builder =>
            {
                builder.AddWork(WorkDefinition.Create("overview.limit.complete", category: "Overview"), SuccessfulWork);
                builder.AddWork(
                    WorkDefinition.Create("overview.limit.failed", category: "Overview"),
                    (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("overview.failed", "Failed.")])));
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        for (var index = 0; index < 6; index++)
        {
            await (await system.Queue.Enqueue("overview.limit.complete", WorkInput.Empty)).WaitForCompletion();
            await (await system.Queue.Enqueue("overview.limit.failed", WorkInput.Empty)).WaitForCompletion();
        }

        var overview = await system.Query.SystemDetails();

        Assert.Equal(6, overview.IterationCountByStatus[WorkCompletionStatus.Completed]);
        Assert.Equal(6, overview.IterationCountByStatus[WorkCompletionStatus.Failed]);
        Assert.Equal(5, overview.CompletedIterations.Count);
        Assert.Equal(5, overview.FailedIterations.Count);
        Assert.Equal(5, overview.FailedWorkers.Count);
        Assert.All(overview.CompletedIterations, iteration => Assert.Equal(WorkCompletionStatus.Completed, iteration.Status));
        Assert.All(overview.FailedIterations, iteration => Assert.Equal(WorkCompletionStatus.Failed, iteration.Status));
        Assert.All(overview.FailedWorkers, worker => Assert.Equal(WorkerState.Failed, worker.State));
    }

    [Fact]
    public async Task SystemDetailsQueryReturnsTopCommonKeyTypesWithDistinctIterationCounts()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("overview.keys", "Adds overview keys."),
            SuccessfulWork);
        await system.Start();

        await (await system.Queue.Enqueue(
            "overview.keys",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-1"))
                .WithConcurrencyKey(new WorkConcurrencyKey("claim", "CLM-1"))
                .WithIdentifier(new WorkIdentifier("claim", "CLM-1")))).WaitForCompletion();
        await (await system.Queue.Enqueue(
            "overview.keys",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("claim", "CLM-2")))).WaitForCompletion();
        await (await system.Queue.Enqueue(
            "overview.keys",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("customer", "CUST-1")))).WaitForCompletion();
        for (var index = 0; index < 11; index++)
        {
            await (await system.Queue.Enqueue(
                "overview.keys",
                WorkInput.Empty.WithIdentifier(new WorkIdentifier($"secondary-{index}", index.ToString())))).WaitForCompletion();
        }

        var overview = await system.Query.SystemDetails();

        Assert.Equal(10, overview.CommonKeyTypes.Count);
        var claim = Assert.Single(overview.CommonKeyTypes, keyType => keyType.Type == "claim");
        Assert.Equal(2, claim.IterationCount);
        Assert.Equal(1, claim.IterationCountByKind[WorkKeyKind.Subject]);
        Assert.Equal(1, claim.IterationCountByKind[WorkKeyKind.ConcurrencyKey]);
        Assert.Equal(2, claim.IterationCountByKind[WorkKeyKind.Identifier]);
        Assert.Contains(overview.CommonKeyTypes, keyType => keyType.Type == "customer" && keyType.IterationCount == 1);
    }

    [Fact]
    public async Task SystemDetailsQueryCountsCommonKeyTypesByIterationNotWorker()
    {
        var attempts = 0;
        var definition = WorkDefinition.Create("overview.retry.keys", "Retries with the same key.");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                definition,
                (context, input, cancellationToken) =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        throw new TimeoutException("Try again.");
                    }

                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration
                    .RetryTransientFailures(
                        count: 1,
                        initialDelay: TimeSpan.FromMilliseconds(1),
                        jitter: TimeSpan.Zero)
                    .ClassifyExceptions(exception => exception is TimeoutException
                        ? WorkExceptionClassification.Transient
                        : WorkExceptionClassification.Unknown)))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        await (await system.Queue.Enqueue(
            "overview.retry.keys",
            WorkInput.Empty.WithSubject(new WorkSubjectId("claim", "CLM-1")))).WaitForCompletion();

        var overview = await system.Query.SystemDetails();

        var claim = Assert.Single(overview.CommonKeyTypes, keyType => keyType.Type == "claim");
        Assert.Equal(2, claim.IterationCount);
        Assert.Equal(2, claim.IterationCountByKind[WorkKeyKind.Subject]);
        Assert.Equal(1, overview.CompletedIterationCount);
        Assert.Equal(1, overview.FailedIterationCount);
    }

    [Fact]
    public async Task SystemDetailsQueryCanScopeToCategoryOrDefinition()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem("overview-scope", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("billing.root", category: "Billing"),
                    SuccessfulWork);
                builder.AddWork(
                    WorkDefinition.Create("billing.invoice.failed", category: "Billing:Invoices"),
                    (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("billing.failed", "Failed.")])));
                builder.AddWork(
                    WorkDefinition.Create("shipping.complete", category: "Shipping"),
                    SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        await (await system.Queue.Enqueue(
            "billing.root",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("account", "A-1")))).WaitForCompletion();
        await (await system.Queue.Enqueue(
            "billing.invoice.failed",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("invoice", "I-1")))).WaitForCompletion();
        await (await system.Queue.Enqueue(
            "shipping.complete",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("shipment", "S-1")))).WaitForCompletion();

        var billing = await system.Query.SystemDetails(new WorkSystemCriteria(Category: "Billing"));
        var exactBilling = await system.Query.SystemDetails(new WorkSystemCriteria(
            Category: "Billing",
            IncludeSubcategories: false));
        var invoice = await system.Query.SystemDetails(new WorkSystemCriteria(DefinitionName: "billing.invoice.failed"));

        Assert.Equal(1, billing.CompletedIterationCount);
        Assert.Equal(1, billing.FailedIterationCount);
        Assert.Equal(1, billing.FinalWorkerCount);
        Assert.Equal(1, billing.FailedWorkerCount);
        Assert.Equal(["billing.invoice.failed"], billing.FailedWorkers.Select(worker => worker.DefinitionName));
        Assert.Contains(billing.CommonKeyTypes, keyType => keyType.Type == "account" && keyType.IterationCount == 1);
        Assert.Contains(billing.CommonKeyTypes, keyType => keyType.Type == "invoice" && keyType.IterationCount == 1);
        Assert.DoesNotContain(billing.CommonKeyTypes, keyType => keyType.Type == "shipment");

        Assert.Equal(1, exactBilling.CompletedIterationCount);
        Assert.Equal(0, exactBilling.FailedIterationCount);
        Assert.Empty(exactBilling.FailedWorkers);
        var exactKeyType = Assert.Single(exactBilling.CommonKeyTypes);
        Assert.Equal("account", exactKeyType.Type);

        Assert.Equal(0, invoice.CompletedIterationCount);
        Assert.Equal(1, invoice.FailedIterationCount);
        Assert.Equal("billing.invoice.failed", Assert.Single(invoice.FailedWorkers).DefinitionName);
    }

    [Fact]
    public async Task SystemDetailsQueryReportsOldestQueuedWorkerWithinScope()
    {
        var queuedConfiguration = WorkerOptionFixtures.DoNotStart().Configuration;
        await using var system = new ServiceCollection()
            .AddWorkableSystem("overview-queue-pressure", builder =>
            {
                builder.AddWork(WorkDefinition.Create("queue.billing.root", category: "Billing", configuration: queuedConfiguration), SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("queue.billing.invoice", category: "Billing:Invoices", configuration: queuedConfiguration), SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("queue.shipping", category: "Shipping", configuration: queuedConfiguration), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        var shippingHandle = await system.Queue.Enqueue("queue.shipping");
        await Task.Delay(10);
        var billingHandle = await system.Queue.Enqueue("queue.billing.root");
        await Task.Delay(10);
        var invoiceHandle = await system.Queue.Enqueue("queue.billing.invoice");

        var shipping = await system.Query.Worker(RequiredWorkerId(shippingHandle))
            ?? throw new InvalidOperationException("Expected shipping worker.");
        var billingRoot = await system.Query.Worker(RequiredWorkerId(billingHandle))
            ?? throw new InvalidOperationException("Expected billing worker.");
        var billingInvoice = await system.Query.Worker(RequiredWorkerId(invoiceHandle))
            ?? throw new InvalidOperationException("Expected billing invoice worker.");

        var wholeSystem = await system.Query.SystemDetails();
        var billing = await system.Query.SystemDetails(new WorkSystemCriteria(Category: "Billing"));
        var exactBilling = await system.Query.SystemDetails(new WorkSystemCriteria(
            Category: "Billing",
            IncludeSubcategories: false));
        var invoice = await system.Query.SystemDetails(new WorkSystemCriteria(DefinitionName: "queue.billing.invoice"));

        Assert.Equal(shipping.StateChangedAt, wholeSystem.OldestQueuedAt);
        Assert.Equal(billingRoot.StateChangedAt, billing.OldestQueuedAt);
        Assert.Equal(billingRoot.StateChangedAt, exactBilling.OldestQueuedAt);
        Assert.Equal(billingInvoice.StateChangedAt, invoice.OldestQueuedAt);

        var cancelRoot = await system.Workers.Execute(billingRoot.Version, WorkAction.Cancel);
        var billingAfterCancel = await system.Query.SystemDetails(new WorkSystemCriteria(Category: "Billing"));

        Assert.True(cancelRoot.IsAccepted);
        Assert.Equal(billingInvoice.StateChangedAt, billingAfterCancel.OldestQueuedAt);
    }

    [Fact]
    public async Task SystemThroughputQueryReportsIterationLifecycleEventsWithinScope()
    {
        var cancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var system = new ServiceCollection()
            .AddWorkableSystem("overview-throughput", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("metrics.success", category: "Metrics:Included"),
                    SuccessfulWork);
                builder.AddWork(
                    WorkDefinition.Create("metrics.failed", category: "Metrics:Included"),
                    (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("metrics.failed", "Failed.")])));
                builder.AddWork(
                    WorkDefinition.Create("metrics.canceled", category: "Metrics:Included"),
                    async (_, _, cancellationToken) =>
                    {
                        cancelStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWork(
                    WorkDefinition.Create("metrics.other", category: "Metrics:Other"),
                    SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();
        await (await system.Queue.Enqueue("metrics.success")).WaitForCompletion();
        await (await system.Queue.Enqueue("metrics.failed")).WaitForCompletion();
        var canceled = await system.Queue.Enqueue("metrics.canceled");
        await cancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var canceledWorker = await system.Query.Worker(RequiredWorkerId(canceled))
            ?? throw new InvalidOperationException("Expected canceled worker.");
        await system.Workers.Execute(canceledWorker.Version, WorkAction.Cancel);
        await canceled.WaitForCompletion();
        await (await system.Queue.Enqueue("metrics.other")).WaitForCompletion();
        await WaitForThroughputBucketToClose();

        var overviewWithoutThroughput = await system.Query.SystemDetails(new WorkSystemCriteria(
            Category: "Metrics:Included"));
        var overview = await system.Query.SystemDetails(new WorkSystemCriteria(
            Category: "Metrics:Included",
            IncludeThroughput: true));
        var throughput = await system.Query.SystemThroughput(new WorkSystemCriteria(Category: "Metrics:Included"),
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1));

        Assert.Null(overviewWithoutThroughput.Throughput);
        Assert.NotNull(overview.Throughput);
        Assert.Equal(3, overview.Throughput.Buckets.Sum(bucket => bucket.Started));
        Assert.Equal(1, overview.Throughput.Buckets.Sum(bucket => bucket.Completed));
        Assert.Equal(1, overview.Throughput.Buckets.Sum(bucket => bucket.Failed));
        Assert.Equal(1, overview.Throughput.Buckets.Sum(bucket => bucket.Canceled));
        Assert.Equal(3, throughput.Buckets.Sum(bucket => bucket.Started));
        Assert.Equal(1, throughput.Buckets.Sum(bucket => bucket.Completed));
        Assert.Equal(1, throughput.Buckets.Sum(bucket => bucket.Failed));
        Assert.Equal(1, throughput.Buckets.Sum(bucket => bucket.Canceled));
        Assert.Equal(3, throughput.SettledCount);
        Assert.Equal(60, throughput.LiveSummary.WindowSeconds);
        Assert.Equal(3 / 60.0, throughput.LiveSummary.StartedPerSecond, precision: 6);
        Assert.Equal(1 / 60.0, throughput.LiveSummary.CompletedPerSecond, precision: 6);
        Assert.Equal(1 / 60.0, throughput.LiveSummary.FailedPerSecond, precision: 6);
        Assert.Equal(1 / 60.0, throughput.LiveSummary.CanceledPerSecond, precision: 6);
        Assert.Equal(0, throughput.LiveSummary.InFlightDeltaPerSecond, precision: 6);
        Assert.All(throughput.Buckets.Where(bucket => bucket.Completed > 0 || bucket.Failed > 0 || bucket.Canceled > 0), bucket =>
            Assert.True(bucket.AverageExecutionMilliseconds >= 0));
    }

    [Fact]
    public async Task SystemThroughputQueryRetainsMetricsAfterWorkerPurge()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("metrics.purge", category: "Metrics:Purge"),
            SuccessfulWork);

        await system.Start();
        var handle = await system.Queue.Enqueue("metrics.purge");
        await handle.WaitForCompletion();
        await WaitForThroughputBucketToClose();

        var workerId = RequiredWorkerId(handle);
        var beforePurge = await system.Query.SystemThroughput(new WorkSystemCriteria(Category: "Metrics:Purge"),
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1));
        var worker = await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");

        var purge = await system.Workers.Execute(worker.Version, WorkAction.Purge);

        var afterPurgeIterations = await system.Query.WorkerIterations(new WorkerIterationCriteria(WorkerId: workerId));
        var afterPurge = await system.Query.SystemThroughput(new WorkSystemCriteria(Category: "Metrics:Purge"),
            new WorkThroughputCriteria(WindowSeconds: 60, BucketSeconds: 1));

        Assert.True(purge.IsAccepted);
        Assert.Equal(1, beforePurge.Buckets.Sum(bucket => bucket.Started));
        Assert.Equal(1, beforePurge.Buckets.Sum(bucket => bucket.Completed));
        Assert.Empty(afterPurgeIterations.Iterations);
        Assert.Equal(1, afterPurge.Buckets.Sum(bucket => bucket.Started));
        Assert.Equal(1, afterPurge.Buckets.Sum(bucket => bucket.Completed));
        Assert.Equal(1 / 60.0, afterPurge.LiveSummary.StartedPerSecond, precision: 6);
        Assert.Equal(1 / 60.0, afterPurge.LiveSummary.CompletedPerSecond, precision: 6);
        Assert.Equal(0, afterPurge.LiveSummary.InFlightDeltaPerSecond, precision: 6);
    }

    [Fact]
    public async Task QueryReadModelClearsWhenSystemStops()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("readmodel.clear", category: "ReadModel"),
            SuccessfulWork);

        await system.Start();
        await (await system.Queue.Enqueue("readmodel.clear")).WaitForCompletion();

        var beforeStop = await system.Query.Workers();
        await system.Stop();
        var afterStop = await system.Query.Workers();
        var overview = await system.Query.SystemDetails();

        Assert.NotEmpty(beforeStop.Workers);
        Assert.Empty(afterStop.Workers);
        Assert.Equal(0, overview.ActiveWorkerCount);
        Assert.Empty(overview.WorkerCountByState);
    }

    [Fact]
    public async Task QueryReadModelDiagnosticsReportProjectionProgress()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("readmodel.diagnostics", category: "ReadModel"),
            SuccessfulWork);

        var initial = system.Diagnostics.ReadModel;
        await system.Start();
        var handle = await system.Queue.Enqueue("readmodel.diagnostics");
        await handle.WaitForCompletion();

        await system.Query.Worker(RequiredWorkerId(handle));
        var diagnostics = system.Diagnostics.ReadModel;

        Assert.Equal(0, initial.EnqueuedSequence);
        Assert.Equal(0, initial.AppliedSequence);
        Assert.Equal(0, initial.PendingUpdateCount);
        Assert.True(diagnostics.EnqueuedSequence > 0);
        Assert.Equal(diagnostics.EnqueuedSequence, diagnostics.AppliedSequence);
        Assert.Equal(0, diagnostics.PendingUpdateCount);
        Assert.True(diagnostics.AppliedUpdateCount > 0);
        Assert.True(diagnostics.PublishedSnapshotCount > 0);
        Assert.True(diagnostics.LastBatchSize > 0);
        Assert.True(diagnostics.LastProjectionDuration >= TimeSpan.Zero);
        Assert.NotNull(diagnostics.LastProjectedAt);
        Assert.False(diagnostics.HasProjectorFailure);
        Assert.Null(diagnostics.ProjectorFailureType);
        Assert.Null(diagnostics.ProjectorFailureMessage);
    }

    [Fact]
    public async Task WorkerOperationsReadFromReadModel()
    {
        var subject = new WorkSubjectId("read-model", "worker-operations");
        var definition = WorkDefinition.Create("readmodel.worker-operations", category: "ReadModel");
        await using var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();
        var handle = await system.Queue.Enqueue(
            "readmodel.worker-operations",
            WorkInput.Empty.WithSubject(subject));
        await handle.WaitForCompletion();
        var workerId = RequiredWorkerId(handle);
        var operations = Assert.IsType<WorkerOperations>(system.Workers);

        var byId = await operations.Get(workerId);
        var all = await operations.List();
        var bySubject = await operations.List(subject);
        var byDefinitionAndSubject = await operations.List(definition.Id, subject);
        await system.Stop();

        var afterStopById = await operations.Get(workerId);
        var afterStopAll = await operations.List();

        Assert.NotNull(byId);
        Assert.Equal(workerId, byId.Id);
        Assert.Contains(all, worker => worker.Id == workerId);
        Assert.Equal(workerId, Assert.Single(bySubject).Id);
        Assert.Equal(workerId, Assert.Single(byDefinitionAndSubject).Id);
        Assert.Null(afterStopById);
        Assert.Empty(afterStopAll);
    }

    private static async Task WaitForThroughputBucketToClose()
    {
        var currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= currentSecond)
        {
            await Task.Delay(10);
        }
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

    [WorkMetadata("employee.onboard", "People:Onboarding", "Creates onboarding tasks for a new employee.")]
    private sealed class AttributedMetadataWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
