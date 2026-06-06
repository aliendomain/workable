using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class AuthorizedWorkQueueServiceShould
{
    [Fact]
    public async Task ReturnNotFoundWithoutCallingInnerForUnknownDefinitions()
    {
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            out _,
            out _,
            out var inner);
        var missingId = WorkDefinitionId.New();

        var byId = await queue.Enqueue(missingId);
        var byName = await queue.Enqueue("missing.work");

        Assert.Equal(WorkQueueStatus.NotFound, byId.QueueOutcome.Status);
        Assert.Null(byId.QueueOutcome.DefinitionId);
        Assert.Equal(WorkQueueStatus.NotFound, byName.QueueOutcome.Status);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task ReturnUnauthorizedWithoutCallingInnerForInoperableDefinitions()
    {
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            out _,
            out var hidden,
            out var inner);

        var byId = await queue.Enqueue(hidden.Id);
        var byName = await queue.Enqueue(hidden.Name);

        Assert.Equal(WorkQueueStatus.Unauthorized, byId.QueueOutcome.Status);
        Assert.Equal(hidden.Id, byId.QueueOutcome.DefinitionId);
        Assert.Equal(WorkQueueStatus.Unauthorized, byName.QueueOutcome.Status);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task DelegateEveryEnqueueOverloadForOperableDefinitions()
    {
        var queue = CreateQueueService(
            groups: ["visible.operate"],
            out var visible,
            out _,
            out var inner);
        var input = WorkInput.FromValue(new QueueInput("direct"), WorkData.DefaultJsonOptions);
        var typedById = new QueueInput("typed-id");
        var typedByName = new QueueInput("typed-name");
        var options = WorkerOptions.Default with { ProfilingEnabled = true };
        using var cancellation = new CancellationTokenSource();

        await queue.Enqueue(visible.Id, input, options, cancellation.Token);
        await queue.Enqueue(visible.Id, typedById, options, cancellation.Token);
        await queue.Enqueue(visible.Name, input, options, cancellation.Token);
        await queue.Enqueue(visible.Name, typedByName, options, cancellation.Token);

        Assert.Equal(
            [
                new RecordedQueueCall("id", visible.Id, null, input, options, cancellation.Token),
                new RecordedQueueCall("id-typed", visible.Id, null, typedById, options, cancellation.Token),
                new RecordedQueueCall("name", null, visible.Name, input, options, cancellation.Token),
                new RecordedQueueCall("name-typed", null, visible.Name, typedByName, options, cancellation.Token),
            ],
            inner.Calls);
    }

    private static AuthorizedWorkQueueService CreateQueueService(
        IReadOnlyList<string> groups,
        out WorkDefinition visible,
        out WorkDefinition hidden,
        out RecordingWorkQueueService inner)
    {
        visible = CreateDefinition("visible.work", "visible.operate");
        hidden = CreateDefinition("hidden.work", "hidden.operate");
        var catalog = new WorkSystemCatalog(
            [
                CreateRegisteredWork(visible),
                CreateRegisteredWork(hidden),
            ],
            persistenceStoreAvailable: false);
        inner = new RecordingWorkQueueService();
        return new AuthorizedWorkQueueService(
            catalog,
            inner,
            new WorkAuthorizationEvaluator(catalog, Groups(groups), false));
    }

    private static WorkDefinition CreateDefinition(string name, string operateGroup)
        => WorkDefinition.Create(
            name,
            authorization: WorkDefinitionAuthorization.Create(
                readGroups: [operateGroup],
                operateGroups: [operateGroup]));

    private static RegisteredWork CreateRegisteredWork(WorkDefinition definition)
        => new(definition, _ => new NoopExecutor(), []);

    private static IReadOnlySet<string> Groups(IEnumerable<string> groups)
        => groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed record QueueInput(string Value);

    private sealed record RecordedQueueCall(
        string Overload,
        WorkDefinitionId? DefinitionId,
        string? Name,
        object? Input,
        WorkerOptions? Options,
        CancellationToken CancellationToken);

    private sealed class RecordingWorkQueueService : IWorkQueueService
    {
        public List<RecordedQueueCall> Calls { get; } = [];

        public Task<IWorkerHandle> Enqueue(
            WorkDefinitionId definitionId,
            WorkInput? input = null,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(new("id", definitionId, null, input, options, cancellationToken));
            return Accepted(definitionId);
        }

        public Task<IWorkerHandle> Enqueue<TInput>(
            WorkDefinitionId definitionId,
            TInput input,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(new("id-typed", definitionId, null, input, options, cancellationToken));
            return Accepted(definitionId);
        }

        public Task<IWorkerHandle> Enqueue(
            string name,
            WorkInput? input = null,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(new("name", null, name, input, options, cancellationToken));
            return Accepted();
        }

        public Task<IWorkerHandle> Enqueue<TInput>(
            string name,
            TInput input,
            WorkerOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls.Add(new("name-typed", null, name, input, options, cancellationToken));
            return Accepted();
        }

        private static Task<IWorkerHandle> Accepted(WorkDefinitionId? definitionId = null)
            => Task.FromResult<IWorkerHandle>(new RecordingWorkerHandle(
                WorkQueueOutcome.Accepted(definitionId ?? WorkDefinitionId.New(), WorkerId.New())));
    }

    private sealed class RecordingWorkerHandle(WorkQueueOutcome queueOutcome) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = queueOutcome;

        public WorkerId? WorkerId => this.QueueOutcome.WorkerId;

        public Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
