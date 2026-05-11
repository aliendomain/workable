using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Query")]
public sealed class WorkQueryTests
{
    [Fact]
    public async Task GetWorkerReturnsFullSnapshot()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("snapshot.work", "Can be retrieved."),
            SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("snapshot.work", WorkInput.Empty);
        var worker = await system.Query.GetWorker(RequiredWorkerId(handle));

        Assert.NotNull(worker);
        Assert.Equal(RequiredWorkerId(handle), worker.Id);
        Assert.Equal("snapshot.work", worker.DefinitionName);
    }

    [Fact]
    public async Task QueryWorkersReturnsSummariesFilteredByDefinitionSubjectConcurrencyKeyAndIdentifier()
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

        var result = await system.Query.QueryWorkers(new WorkerQuery(
            DefinitionId: definition.Id,
            SubjectId: subject,
            ConcurrencyKey: key,
            Identifier: identifier));

        var onlyWorker = Assert.Single(result.Workers);
        Assert.Equal(RequiredWorkerId(accepted), onlyWorker.Id);
        Assert.Equal("invoice.sync", onlyWorker.DefinitionName);
        Assert.Equal("Finance:Invoices", onlyWorker.DefinitionCategory);
        Assert.Contains(identifier, onlyWorker.Identifiers);
    }

    [Fact]
    public async Task QueryWorkersCanFindIdentifiersDiscoveredDuringExecution()
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

        var result = await system.Query.QueryWorkers(new WorkerQuery(Identifier: discovered));

        var onlyWorker = Assert.Single(result.Workers);
        Assert.Equal(RequiredWorkerId(handle), onlyWorker.Id);
        Assert.Contains(discovered, onlyWorker.Identifiers);
    }

    [Fact]
    public async Task QueryWorkersCanFilterByWorkerState()
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

        var running = await system.Query.QueryWorkers(new WorkerQuery(
            States: new HashSet<WorkerState> { WorkerState.Running }));

        release.TrySetResult();
        await handle.WaitForCompletion();
        var completed = await system.Query.QueryWorkers(new WorkerQuery(
            States: new HashSet<WorkerState> { WorkerState.Completed }));

        var onlyRunningWorker = Assert.Single(running.Workers);
        Assert.Equal(RequiredWorkerId(handle), onlyRunningWorker.Id);
        Assert.Equal(WorkerState.Running, onlyRunningWorker.State);
        Assert.Equal(RequiredWorkerId(handle), Assert.Single(completed.Workers).Id);
    }

    [Fact]
    public async Task GetWorkInfoReturnsDefinitionStatusAndWorkerRollup()
    {
        var definition = WorkDefinition.Create("rollup.work", "Reports worker counts.",
            category: "Operations:Rollups");
        await using var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        await system.Queue.Enqueue("rollup.work", WorkInput.Empty);
        var info = await system.Query.GetWorkInfo("rollup.work");

        Assert.NotNull(info);
        Assert.Equal(definition.Id, info.Definition.Id);
        Assert.Equal("Operations:Rollups", info.Definition.Category);
        Assert.Equal(1, info.Workers.Total);
        Assert.True(info.Status is WorkDefinitionStatus.Healthy or WorkDefinitionStatus.Inactive);
    }

    [Fact]
    public async Task QueryWorkDefinitionsFiltersByCategoryAndSearch()
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

        var definitions = await system.Query.QueryWorkDefinitions(new WorkDefinitionQuery(
            Category: "Finance",
            Search: "invoice"));

        var onlyDefinition = Assert.Single(definitions);
        Assert.Equal("invoice.send", onlyDefinition.Name);
    }

    [Fact]
    public async Task QueryWorkDefinitionsUsesCategoryPathWithoutMatchingSimilarNames()
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

        var definitions = await system.Query.QueryWorkDefinitions(new WorkDefinitionQuery(Category: "finance"));

        Assert.Equal(["finance.root", "invoice.send"], definitions.Select(definition => definition.Name));
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

        var onlyDefinition = Assert.Single(await system.Query.QueryWorkDefinitions(new WorkDefinitionQuery(Category: "People:Onboarding")));

        Assert.Equal("employee.onboard", onlyDefinition.Name);
        Assert.Equal("People:Onboarding", onlyDefinition.Category);
        Assert.Equal("Creates onboarding tasks for a new employee.", onlyDefinition.Description);
    }

    [Fact]
    public async Task GetWorkerStatusSummaryReturnsCounts()
    {
        await using var system = CreateSystem(
            WorkDefinition.Create("status.work", "Summarizes status."),
            SuccessfulWork);

        await system.Start();

        await system.Queue.Enqueue("status.work", WorkInput.Empty);

        var summary = await system.Query.GetWorkerStatusSummary();

        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Counts.Values.Sum());
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
