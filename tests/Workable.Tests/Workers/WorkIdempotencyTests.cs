using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Idempotency")]
public sealed class WorkIdempotencyTests
{
    [Fact]
    public void DefaultIdempotencyIsDisabled()
    {
        Assert.False(WorkIdempotencyConfiguration.Default.IsEnabled);
        Assert.Equal(WorkIdempotencyConflictPolicy.RejectDuplicates, WorkIdempotencyConfiguration.Default.ConflictPolicy);
    }

    [Fact]
    public void AttributeConfiguresIdempotency()
    {
        var definition = WorkDefinition.Create("attributed-idempotency", "Uses idempotency attribute.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<AttributedIdempotentWork>(definition))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "attributed-idempotency");

        Assert.True(configured.Configuration.Idempotency.IsEnabled);
    }

    [Fact]
    public void BootstrapConfigurationCanRejectDuplicateSubjects()
    {
        var definition = WorkDefinition.Create("bootstrap-idempotency", "Uses bootstrap config.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<SuccessfulExecutor>(
                definition,
                configuration => configuration.RejectDuplicateSubjects()))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var configured = RequiredDefinition(system, "bootstrap-idempotency");

        Assert.True(configured.Configuration.Idempotency.IsEnabled);
    }

    [Fact]
    public async Task SubjectWithoutIdempotencyAllowsDuplicateWorkers()
    {
        var subject = Subject("customer-1");
        var definition = WorkDefinition.Create("subject-only", "Subject is correlation only.");
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var first = await system.Queue.Enqueue("subject-only", SubjectInput(subject));
        var second = await system.Queue.Enqueue("subject-only", SubjectInput(subject));
        var matches = (await system.Query.QueryWorkers(new WorkerQuery(DefinitionId: definition.Id, SubjectId: subject, Take: 10))).Workers;

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.True(second.QueueOutcome.IsAccepted);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public async Task IdempotencyRejectsMissingSubject()
    {
        var definition = WorkDefinition.Create("missing-subject", "Idempotency requires a subject.",
            configuration: IdempotentConfiguration());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("missing-subject");
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Equal(WorkCompletionStatus.Invalid, completion.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message =>
            message.Code == "workable.idempotency.subject_required" &&
            message.Target == "input.subjectId");
    }

    [Fact]
    public async Task IdempotencyRejectsDuplicateQueuedSubject()
    {
        var subject = Subject("queued");
        var definition = WorkDefinition.Create("duplicate-queued", "Rejects duplicate queued subject.",
            configuration: IdempotentConfiguration() with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var first = await system.Queue.Enqueue("duplicate-queued", SubjectInput(subject));
        var second = await system.Queue.Enqueue("duplicate-queued", SubjectInput(subject));

        Assert.True(first.QueueOutcome.IsAccepted);
        AssertDuplicateSubject(second);
    }

    [Fact]
    public async Task IdempotencyRejectsDuplicateRunningSubject()
    {
        var subject = Subject("running");
        var running = CreateSignal();
        var definition = WorkDefinition.Create("duplicate-running", "Rejects duplicate running subject.",
            configuration: IdempotentConfiguration());
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            running.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        var first = await system.Queue.Enqueue("duplicate-running", SubjectInput(subject));
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await system.Queue.Enqueue("duplicate-running", SubjectInput(subject));

        Assert.True(first.QueueOutcome.IsAccepted);
        AssertDuplicateSubject(second);
    }

    [Fact]
    public async Task IdempotencyRejectsDuplicateCompletedSubject()
    {
        var subject = Subject("completed");
        var definition = WorkDefinition.Create("duplicate-completed", "Rejects duplicate completed subject.",
            configuration: IdempotentConfiguration());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var first = await system.Queue.Enqueue("duplicate-completed", SubjectInput(subject));
        await first.WaitForCompletion();
        var second = await system.Queue.Enqueue("duplicate-completed", SubjectInput(subject));

        AssertDuplicateSubject(second);
    }

    [Fact]
    public async Task IdempotencyRejectsDuplicateFailedSubject()
    {
        var subject = Subject("failed");
        var definition = WorkDefinition.Create("duplicate-failed", "Rejects duplicate failed subject.",
            configuration: IdempotentConfiguration());
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("test.failure", "The work failed.")])));

        await system.Start();

        var first = await system.Queue.Enqueue("duplicate-failed", SubjectInput(subject));
        await first.WaitForCompletion();
        var second = await system.Queue.Enqueue("duplicate-failed", SubjectInput(subject));

        AssertDuplicateSubject(second);
    }

    [Fact]
    public async Task IdempotencyAllowsDuplicateCanceledSubject()
    {
        var subject = Subject("canceled");
        var definition = WorkDefinition.Create("duplicate-canceled", "Canceled workers do not block subject reuse.",
            configuration: IdempotentConfiguration() with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var first = await system.Queue.Enqueue("duplicate-canceled", SubjectInput(subject));
        var firstWorker = RequiredWorker(await system.Query.GetWorker(RequiredWorkerId(first)));
        var cancel = await system.Workers.Execute(firstWorker.Version, WorkAction.Cancel);
        var second = await system.Queue.Enqueue("duplicate-canceled", SubjectInput(subject));
        var matches = (await system.Query.QueryWorkers(new WorkerQuery(DefinitionId: definition.Id, SubjectId: subject, Take: 10))).Workers;

        Assert.True(cancel.IsAccepted);
        Assert.True(second.QueueOutcome.IsAccepted);
        Assert.Equal(2, matches.Count);
        Assert.Equal(RequiredWorkerId(second), matches[0].Id);
    }

    [Fact]
    public async Task QueryBySubjectReturnsWorkersAcrossDefinitionsNewestFirst()
    {
        var subject = Subject("shared");
        var firstDefinition = WorkDefinition.Create("subject-query-one", "First work.");
        var secondDefinition = WorkDefinition.Create("subject-query-two", "Second work.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(firstDefinition, SuccessfulWork);
                builder.AddWork(secondDefinition, SuccessfulWork);
            })
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        await system.Start();

        var first = await system.Queue.Enqueue("subject-query-one", SubjectInput(subject));
        await Task.Delay(TimeSpan.FromMilliseconds(5));
        var second = await system.Queue.Enqueue("subject-query-two", SubjectInput(subject));

        var allMatches = (await system.Query.QueryWorkers(new WorkerQuery(SubjectId: subject, Take: 10))).Workers;
        var definitionMatches = (await system.Query.QueryWorkers(new WorkerQuery(DefinitionId: firstDefinition.Id, SubjectId: subject, Take: 10))).Workers;

        Assert.Equal([RequiredWorkerId(second), RequiredWorkerId(first)], allMatches.Select(worker => worker.Id));
        Assert.Equal([RequiredWorkerId(first)], definitionMatches.Select(worker => worker.Id));
    }

    [Fact]
    public async Task EventsIncludeSubject()
    {
        var subject = Subject("event");
        var definition = WorkDefinition.Create("subject-event", "Events expose subject id.",
            configuration: IdempotentConfiguration() with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        await using var subscription = system.Events.Subscribe(new WorkEventFilter(SubjectId: subject, EventType: "worker.queued"));
        await using var reader = subscription.Read().GetAsyncEnumerator();

        var handle = await system.Queue.Enqueue("subject-event", SubjectInput(subject));
        var workEvent = await ReadNext(reader);

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(subject, workEvent.SubjectId);
    }

    private static WorkSubjectId Subject(string value)
        => new("test", value);

    private static WorkInput SubjectInput(WorkSubjectId subjectId)
        => WorkInput.Empty.WithSubject(subjectId);

    private static WorkConfiguration IdempotentConfiguration()
        => WorkConfiguration.Default with
        {
            Idempotency = new WorkIdempotencyConfiguration
            {
                IsEnabled = true,
            },
        };

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static void AssertDuplicateSubject(IWorkerHandle handle)
    {
        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message =>
            message.Code == "workable.idempotency.duplicate_subject" &&
            message.Target == "input.subjectId");
    }

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }

    private static WorkDefinition RequiredDefinition(IWorkSystem system, string name)
        => system.Catalog.TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Expected work definition '{name}' to exist.");

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected the queue to accept a worker.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker to exist.");

    [WorkIdempotency]
    private sealed class AttributedIdempotentWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class SuccessfulExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
