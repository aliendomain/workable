using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workers")]
public sealed class WorkerIndexBranchShould
{
    [Fact]
    public void RegisterSynchronizeQueryAndForgetAcrossPresentAndMissingEntries()
    {
        var index = new WorkerIndex();
        var subject = new WorkSubjectId("invoice", "42");
        var registered = CreateWorker("index.registered", subject);
        var synchronized = CreateWorker("index.synchronized", subject);

        index.Register(registered);
        index.Register(registered);
        index.Synchronize(synchronized);

        Assert.Equal([registered.Id], index.ByDefinition(registered.Work.Definition.Id));
        Assert.Empty(index.ByDefinition(WorkDefinitionId.New()));
        Assert.Contains(registered.Id, index.BySubject(subject));
        Assert.Contains(synchronized.Id, index.BySubject(subject));
        Assert.Empty(index.BySubject(new WorkSubjectId("invoice", "missing")));

        var forget = typeof(WorkerIndex).GetMethod(
            "Forget",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(WorkerId)])
            ?? throw new InvalidOperationException("Expected worker-id forget helper.");
        forget.Invoke(index, [WorkerId.New()]);
        forget.Invoke(index, [registered.Id]);
        Assert.Empty(index.ByDefinition(registered.Work.Definition.Id));

        index.Clear();
        Assert.Empty(index.BySubject(subject));
    }

    private static WorkerRecord CreateWorker(string name, WorkSubjectId subject)
    {
        var definition = WorkDefinition.Create(name);
        var now = DateTimeOffset.UtcNow;
        return new WorkerRecord(
            WorkerId.New(),
            new RegisteredWork(definition, _ => new NoopExecutor(), []),
            WorkInput.FromValue(new { id = 42 }, subjectId: subject),
            WorkerOptions.Default,
            definition.Configuration,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            createdAt: now,
            updatedAt: now);
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
