using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Indexing")]
public sealed class WorkIndexTests
{
    [Fact]
    public async Task WorkerAndIterationIndexesStayConsistentAcrossTransitionsAndPurge()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var system = new ServiceCollection()
            .AddWorkableSystem("index-transitions", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("index.billing.running", category: "Billing:Invoices"),
                    async (context, input, cancellationToken) =>
                    {
                        context.AddIdentifier(new WorkIdentifier("runtime", "running"));
                        entered.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWork(
                    WorkDefinition.Create(
                        "index.billing.queued",
                        category: "Billing",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                        }),
                    SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("index.shipping.completed", category: "Shipping"), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var running = await system.Queue.Enqueue(
            "index.billing.running",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("case", "running"))
                .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "north"))
                .WithIdentifier(new WorkIdentifier("request", "running")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = await system.Queue.Enqueue(
            "index.billing.queued",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("case", "queued"))
                .WithIdentifier(new WorkIdentifier("request", "queued")));
        await (await system.Queue.Enqueue(
            "index.shipping.completed",
            WorkInput.Empty.WithSubject(new WorkSubjectId("case", "shipping")))).WaitForCompletion();

        var runningWorkerId = RequiredWorkerId(running);
        var queuedWorkerId = RequiredWorkerId(queued);

        var activeBilling = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Billing"));
        var exactBillingQueued = await system.Query.QueryWorkers(new WorkerQuery(
            Category: "Billing",
            IncludeSubcategories: false,
            States: new HashSet<WorkerState> { WorkerState.Queued }));
        var runningBilling = await system.Query.QueryWorkers(new WorkerQuery(
            Category: "Billing",
            States: new HashSet<WorkerState> { WorkerState.Running },
            Identifier: new WorkIdentifier("runtime", "running")));
        var executingIterations = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(
            Category: "Billing",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Executing }));

        Assert.Equal(2, activeBilling.ActiveWorkerCount);
        Assert.Equal(2, activeBilling.DefinitionCount);
        Assert.Equal(1, activeBilling.CurrentIterationCount);
        Assert.Equal(runningWorkerId, Assert.Single(runningBilling.Workers).Id);
        Assert.Equal(queuedWorkerId, Assert.Single(exactBillingQueued.Workers).Id);
        Assert.Equal(runningWorkerId, Assert.Single(executingIterations.Iterations).WorkerId);

        var queuedWorker = RequiredWorker(await system.Query.GetWorker(queuedWorkerId));
        var cancelQueued = await system.Workers.Execute(queuedWorker.Version, WorkAction.Cancel);
        Assert.True(cancelQueued.IsAccepted);

        release.TrySetResult();
        await running.WaitForCompletion();

        var settledBilling = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Billing"));
        var noExecutingIterations = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(
            Category: "Billing",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Executing }));

        Assert.Equal(0, settledBilling.ActiveWorkerCount);
        Assert.Equal(0, settledBilling.DefinitionCount);
        Assert.Equal(2, settledBilling.FinalWorkerCount);
        Assert.Equal(1, settledBilling.CompletedIterationCount);
        Assert.Equal(0, settledBilling.CurrentIterationCount);
        Assert.Empty(noExecutingIterations.Iterations);

        await Purge(system, runningWorkerId);
        await Purge(system, queuedWorkerId);

        var afterPurgeWorkers = await system.Query.QueryWorkers(new WorkerQuery(Category: "Billing"));
        var afterPurgeIterations = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(Category: "Billing"));
        var afterPurgeKeys = await system.Query.QueryWorkerKeys(new WorkerKeyQuery(Search: "request"));
        var afterPurgeOverview = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Billing"));

        Assert.Empty(afterPurgeWorkers.Workers);
        Assert.Empty(afterPurgeIterations.Iterations);
        Assert.Empty(afterPurgeKeys.Keys);
        Assert.Equal(0, afterPurgeOverview.ActiveWorkerCount);
        Assert.Equal(0, afterPurgeOverview.FinalWorkerCount);
        Assert.Equal(0, afterPurgeOverview.CompletedIterationCount);
        Assert.Empty(afterPurgeOverview.WorkerCountByState);
        Assert.Empty(afterPurgeOverview.CommonKeyTypes);
    }

    [Fact]
    public async Task ScopedOverviewKeyTypeIndexesCountDistinctIterationsPerDefinition()
    {
        var attempts = 0;
        await using var system = new ServiceCollection()
            .AddWorkableSystem("index-scoped-keys", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("index.billing.retry", category: "Billing:Invoices"),
                    (context, input, cancellationToken) =>
                    {
                        attempts++;
                        if (attempts == 1)
                        {
                            throw new TimeoutException("Retry this work.");
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
                            : WorkExceptionClassification.Unknown));
                builder.AddWork(WorkDefinition.Create("index.shipping.once", category: "Shipping"), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var billing = await system.Queue.Enqueue(
            "index.billing.retry",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("shared", "billing"))
                .WithConcurrencyKey(new WorkConcurrencyKey("shared", "billing"))
                .WithIdentifier(new WorkIdentifier("shared", "billing")));
        await billing.WaitForCompletion();
        await (await system.Queue.Enqueue(
            "index.shipping.once",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("shared", "shipping"))
                .WithConcurrencyKey(new WorkConcurrencyKey("shared", "shipping"))
                .WithIdentifier(new WorkIdentifier("shared", "shipping")))).WaitForCompletion();

        var wholeSystem = await system.Query.GetSystemOverview();
        var billingScope = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Billing"));
        var shippingScope = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Shipping"));

        AssertKeyType(wholeSystem.CommonKeyTypes, "shared", 3, subject: 3, concurrency: 3, identifier: 3);
        AssertKeyType(billingScope.CommonKeyTypes, "shared", 2, subject: 2, concurrency: 2, identifier: 2);
        AssertKeyType(shippingScope.CommonKeyTypes, "shared", 1, subject: 1, concurrency: 1, identifier: 1);

        await Purge(system, RequiredWorkerId(billing));

        var wholeSystemAfterPurge = await system.Query.GetSystemOverview();
        var billingAfterPurge = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Billing"));

        AssertKeyType(wholeSystemAfterPurge.CommonKeyTypes, "shared", 1, subject: 1, concurrency: 1, identifier: 1);
        Assert.DoesNotContain(billingAfterPurge.CommonKeyTypes, keyType => keyType.Type == "shared");
    }

    [Fact]
    public async Task RetainedIterationEvictionPurgesIterationIndexes()
    {
        var attempts = 0;
        await using var system = new ServiceCollection()
            .AddWorkableSystem("index-retained-iterations", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create(
                        "index.retention.retry",
                        category: "Index:Retention",
                        configuration: WorkConfiguration.Default with
                        {
                            Recurrence = WorkRecurrenceConfiguration.Default with
                            {
                                RetainedFailedIterations = 1,
                                RetainedSuccessfulIterations = 1,
                            },
                        }),
                    (_, _, _) =>
                    {
                        attempts++;
                        if (attempts <= 3)
                        {
                            throw new TimeoutException("Retry this work.");
                        }

                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration
                        .RetryTransientFailures(
                            count: 3,
                            initialDelay: TimeSpan.FromMilliseconds(1),
                            jitter: TimeSpan.Zero)
                        .ClassifyExceptions(exception => exception is TimeoutException
                            ? WorkExceptionClassification.Transient
                            : WorkExceptionClassification.Unknown));
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "index.retention.retry",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("retention", "shared"))
                .WithConcurrencyKey(new WorkConcurrencyKey("retention", "shared"))
                .WithIdentifier(new WorkIdentifier("retention", "shared")));
        await handle.WaitForCompletion();
        var workerId = RequiredWorkerId(handle);

        var retained = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(WorkerId: workerId, Take: 10));
        var failed = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(
            WorkerId: workerId,
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Failed },
            Take: 10));
        var completed = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(
            WorkerId: workerId,
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed },
            Take: 10));
        var key = Assert.Single((await system.Query.QueryWorkIterationKeys(new WorkIterationKeyQuery(
            Kind: WorkKeyKind.Identifier,
            Type: "retention",
            Value: "shared"))).Keys);
        var overview = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Index:Retention"));

        Assert.Equal(2, retained.TotalCount);
        Assert.Equal([3L, 4L], retained.Iterations.Select(iteration => iteration.Sequence).Order());
        Assert.Equal(3, Assert.Single(failed.Iterations).Sequence);
        Assert.Equal(4, Assert.Single(completed.Iterations).Sequence);
        Assert.Null(await system.Query.GetWorkerIteration(new WorkerIterationReference(workerId, 1)));
        Assert.Null(await system.Query.GetWorkerIteration(new WorkerIterationReference(workerId, 2)));
        Assert.NotNull(await system.Query.GetWorkerIteration(new WorkerIterationReference(workerId, 3)));
        Assert.NotNull(await system.Query.GetWorkerIteration(new WorkerIterationReference(workerId, 4)));
        Assert.Equal([3L, 4L], key.Iterations.Select(iteration => iteration.Sequence).Order());
        Assert.Equal(1, overview.FailedIterationCount);
        Assert.Equal(1, overview.CompletedIterationCount);
        AssertKeyType(overview.CommonKeyTypes, "retention", 2, subject: 2, concurrency: 2, identifier: 2);
    }

    [Fact]
    public async Task CandidateIndexesIntersectDefinitionStateSubjectAndIdentifier()
    {
        var sharedSubject = new WorkSubjectId("account", "A-1");
        var targetIdentifier = new WorkIdentifier("invoice", "INV-1");
        var otherIdentifier = new WorkIdentifier("invoice", "INV-2");
        var targetDefinition = WorkDefinition.Create(
            "index.intersection.target",
            category: "Billing",
            configuration: WorkerOptionFixtures.DoNotStart().Configuration);
        var otherDefinition = WorkDefinition.Create(
            "index.intersection.other",
            category: "Billing",
            configuration: WorkerOptionFixtures.DoNotStart().Configuration);
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(targetDefinition, SuccessfulWork);
                builder.AddWork(otherDefinition, SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var target = await system.Queue.Enqueue(
            targetDefinition.Name,
            WorkInput.Empty.WithSubject(sharedSubject).WithIdentifier(targetIdentifier));
        var sameSubjectOtherDefinition = await system.Queue.Enqueue(
            otherDefinition.Name,
            WorkInput.Empty.WithSubject(sharedSubject).WithIdentifier(targetIdentifier));
        var sameDefinitionOtherIdentifier = await system.Queue.Enqueue(
            targetDefinition.Name,
            WorkInput.Empty.WithSubject(sharedSubject).WithIdentifier(otherIdentifier));
        var sameKeysCanceled = await system.Queue.Enqueue(
            targetDefinition.Name,
            WorkInput.Empty.WithSubject(sharedSubject).WithIdentifier(targetIdentifier));
        await Cancel(system, RequiredWorkerId(sameKeysCanceled));

        var matches = await system.Query.QueryWorkers(new WorkerQuery(
            DefinitionId: targetDefinition.Id,
            SubjectId: sharedSubject,
            Identifier: targetIdentifier,
            States: new HashSet<WorkerState> { WorkerState.Queued },
            Take: 10));

        Assert.Equal(RequiredWorkerId(target), Assert.Single(matches.Workers).Id);
        Assert.DoesNotContain(matches.Workers, worker => worker.Id == RequiredWorkerId(sameSubjectOtherDefinition));
        Assert.DoesNotContain(matches.Workers, worker => worker.Id == RequiredWorkerId(sameDefinitionOtherIdentifier));
        Assert.DoesNotContain(matches.Workers, worker => worker.Id == RequiredWorkerId(sameKeysCanceled));
    }

    [Fact]
    public async Task MissingIndexedPredicatesReturnEmptyResults()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem("index-missing-candidates", builder =>
            {
                builder.AddWork(WorkDefinition.Create("index.missing.candidates", category: "Index:Missing"), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        await (await system.Queue.Enqueue(
            "index.missing.candidates",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("account", "A-1"))
                .WithIdentifier(new WorkIdentifier("invoice", "INV-1")))).WaitForCompletion();

        var workers = await system.Query.QueryWorkers(new WorkerQuery(
            SubjectId: new WorkSubjectId("account", "missing"),
            States: new HashSet<WorkerState> { WorkerState.Completed }));
        var iterations = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(
            Identifier: new WorkIdentifier("invoice", "missing"),
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed }));
        var categoryWorkers = await system.Query.QueryWorkers(new WorkerQuery(Category: "Does:Not:Exist"));
        var categoryIterations = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(Category: "Does:Not:Exist"));

        Assert.Empty(workers.Workers);
        Assert.Empty(iterations.Iterations);
        Assert.Empty(categoryWorkers.Workers);
        Assert.Empty(categoryIterations.Iterations);
    }

    [Fact]
    public async Task KeyIndexesSupportDirectLookupAndKindScopedTypeFacets()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem("index-key-lookups", builder =>
            {
                builder.AddWork(WorkDefinition.Create("index.key.lookup", category: "Index:Keys"), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "index.key.lookup",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-1"))
                .WithConcurrencyKey(new WorkConcurrencyKey("claim", "CLM-1"))
                .WithIdentifier(new WorkIdentifier("claim", "CLM-1")));
        await handle.WaitForCompletion();
        var workerId = RequiredWorkerId(handle);

        var workerKeys = await system.Query.QueryWorkerKeys(new WorkerKeyQuery(
            Kind: WorkKeyKind.Identifier,
            Type: "CLAIM",
            Value: "clm-1"));
        var workerKeyTypes = await system.Query.QueryWorkerKeyTypes(new WorkerKeyTypeQuery(
            Kind: WorkKeyKind.Subject,
            Type: "CLAIM"));
        var iterationKeys = await system.Query.QueryWorkIterationKeys(new WorkIterationKeyQuery(
            Kind: WorkKeyKind.Identifier,
            Type: "CLAIM",
            Value: "clm-1"));
        var iterationKeyTypes = await system.Query.QueryWorkIterationKeyTypes(new WorkIterationKeyTypeQuery(
            Kind: WorkKeyKind.Subject,
            Type: "CLAIM"));

        var workerKey = Assert.Single(workerKeys.Keys);
        Assert.Equal(WorkKeyKind.Identifier, workerKey.Kind);
        Assert.Equal(workerId, Assert.Single(workerKey.Workers).Id);

        var workerKeyType = Assert.Single(workerKeyTypes.Types);
        Assert.Equal("claim", workerKeyType.Type);
        Assert.Equal(1, workerKeyType.WorkerCount);
        Assert.Equal(1, Assert.Single(workerKeyType.WorkerCountByKind).Value);
        Assert.Equal(WorkKeyKind.Subject, Assert.Single(workerKeyType.WorkerCountByKind).Key);

        var iterationKey = Assert.Single(iterationKeys.Keys);
        Assert.Equal(WorkKeyKind.Identifier, iterationKey.Kind);
        Assert.Equal(workerId, Assert.Single(iterationKey.Iterations).WorkerId);

        var iterationKeyType = Assert.Single(iterationKeyTypes.Types);
        Assert.Equal("claim", iterationKeyType.Type);
        Assert.Equal(1, iterationKeyType.IterationCount);
        Assert.Equal(1, Assert.Single(iterationKeyType.IterationCountByKind).Value);
        Assert.Equal(WorkKeyKind.Subject, Assert.Single(iterationKeyType.IterationCountByKind).Key);
    }

    [Fact]
    public async Task ReplacingExecutingIterationWithCompletedIterationUpdatesScopedStatusIndexes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var system = new ServiceCollection()
            .AddWorkableSystem("index-status-replacement", builder =>
            {
                builder.AddWork(
                    WorkDefinition.Create("index.status.replace", category: "Index:Status"),
                    async (_, _, cancellationToken) =>
                    {
                        entered.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWork(WorkDefinition.Create("index.status.other", category: "Other"), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var handle = await system.Queue.Enqueue(
            "index.status.replace",
            WorkInput.Empty.WithSubject(new WorkSubjectId("status", "replace")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var executingOverview = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Index"));
        var executingQuery = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(
            Category: "Index",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Executing }));

        Assert.Equal(1, executingOverview.CurrentIterationCount);
        Assert.Equal(1, executingOverview.IterationCountByStatus[WorkCompletionStatus.Executing]);
        Assert.Equal(RequiredWorkerId(handle), Assert.Single(executingQuery.Iterations).WorkerId);

        release.TrySetResult();
        await handle.WaitForCompletion();

        var completedOverview = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Index"));
        var executingAfterCompletion = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(
            Category: "Index",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Executing }));
        var completedAfterCompletion = await system.Query.QueryWorkerIterations(new WorkerIterationQuery(
            Category: "Index",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Completed }));

        Assert.Equal(0, completedOverview.CurrentIterationCount);
        Assert.Equal(1, completedOverview.CompletedIterationCount);
        Assert.False(completedOverview.IterationCountByStatus.ContainsKey(WorkCompletionStatus.Executing));
        Assert.Equal(1, completedOverview.IterationCountByStatus[WorkCompletionStatus.Completed]);
        Assert.Empty(executingAfterCompletion.Iterations);
        Assert.Equal(RequiredWorkerId(handle), Assert.Single(completedAfterCompletion.Iterations).WorkerId);
    }

    [Fact]
    public async Task RecentIterationIndexesReturnNewestScopedIterations()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem("index-recent-iterations", builder =>
            {
                builder.AddWork(WorkDefinition.Create("index.recent.billing", category: "Billing:Recent"), SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("index.recent.shipping", category: "Shipping:Recent"), SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        for (var index = 0; index < 7; index++)
        {
            await (await system.Queue.Enqueue(
                "index.recent.billing",
                WorkInput.Empty.WithIdentifier(new WorkIdentifier("sequence", index.ToString())))).WaitForCompletion();
        }

        await (await system.Queue.Enqueue(
            "index.recent.shipping",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("sequence", "shipping")))).WaitForCompletion();

        var billing = await system.Query.GetSystemOverview(new WorkOverviewQuery(Category: "Billing"));

        Assert.Equal(5, billing.CompletedIterations.Count);
        Assert.All(billing.CompletedIterations, iteration => Assert.Equal("index.recent.billing", iteration.DefinitionName));
        Assert.Equal(
            ["6", "5", "4", "3", "2"],
            billing.CompletedIterations
                .Select(iteration => Assert.Single(iteration.Identifiers, identifier => identifier.Type == "sequence").Value));
    }

    private static async Task Purge(IWorkSystem system, WorkerId workerId)
    {
        var worker = RequiredWorker(await system.Query.GetWorker(workerId));
        var purge = await system.Workers.Execute(worker.Version, WorkAction.Purge);
        Assert.True(purge.IsAccepted);
    }

    private static async Task Cancel(IWorkSystem system, WorkerId workerId)
    {
        var worker = RequiredWorker(await system.Query.GetWorker(workerId));
        var cancel = await system.Workers.Execute(worker.Version, WorkAction.Cancel);
        Assert.True(cancel.IsAccepted);
    }

    private static void AssertKeyType(
        IReadOnlyList<WorkIterationKeyTypeFacet> keyTypes,
        string type,
        int count,
        int subject,
        int concurrency,
        int identifier)
    {
        var keyType = Assert.Single(keyTypes, candidate => candidate.Type == type);
        Assert.Equal(count, keyType.IterationCount);
        Assert.Equal(subject, keyType.IterationCountByKind[WorkKeyKind.Subject]);
        Assert.Equal(concurrency, keyType.IterationCountByKind[WorkKeyKind.ConcurrencyKey]);
        Assert.Equal(identifier, keyType.IterationCountByKind[WorkKeyKind.Identifier]);
    }

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker.");

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker.");

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
