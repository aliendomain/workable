using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Queueing")]
public sealed class QueueAndResultTests
{
    private static readonly AsyncLocal<string?> AmbientRequestValue = new();

    [Fact]
    public async Task QueueByIdReturnsAwaitableHandleAndSerializedOutput()
    {
        var definition = WorkDefinition.Create("echo", "Returns its input.");
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? null : WorkOutput.FromData(input))));

        await system.Start();

        var input = WorkInput.FromValue(new EchoArgs("hello"));
        var handle = await system.Queue.Enqueue(definition.Id, input);
        var completion = await handle.WaitForCompletion();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.NotNull(handle.WorkerId);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.NotNull(completion.Output);
        Assert.Equal("hello", completion.Output.ToValue<EchoArgs>()?.Message);
    }

    [Fact]
    public async Task QueueByNameWorks()
    {
        var definition = WorkDefinition.Create("named-work", "Runs by name.");
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("named-work");

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(definition.Id, handle.QueueOutcome.DefinitionId);
    }

    [Fact]
    public async Task QueueReturnsHandleBeforeExecutorStarts()
    {
        var entered = false;
        var definition = WorkDefinition.Create("deferred-start", "Starts after queue accepts.");
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            Volatile.Write(ref entered, true);
            return Task.FromResult(WorkExecutionResult.Success());
        });

        await system.Start();

        var handle = await system.Queue.Enqueue("deferred-start");

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(Volatile.Read(ref entered));

        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task QueueHandleCanAwaitWorkerThatHasNotStartedYet()
    {
        var definition = WorkDefinition.Create("manual-start", "Waits in the queue until started.",
            defaultOptions: WorkerOptionFixtures.DoNotStart());
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue("manual-start");
        var completionTask = handle.WaitForCompletion();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(completionTask.IsCompleted);

        var workerId = RequiredWorkerId(handle);
        var worker = RequiredWorker(await system.Query.GetWorker(workerId));
        var start = await system.Workers.Execute(worker.Version, WorkAction.Start);
        var completion = await completionTask;

        Assert.True(start.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AcceptedWorkDoesNotUseQueueCancellationTokenForExecution()
    {
        var definition = WorkDefinition.Create("detached", "Runs after queue token cancellation.");
        var system = CreateSystem(definition, async (context, input, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            return WorkExecutionResult.Success();
        });

        await system.Start();

        using var queueCancellation = new CancellationTokenSource();
        var handle = await system.Queue.Enqueue("detached", cancellationToken: queueCancellation.Token);
        queueCancellation.Cancel();

        var completion = await handle.WaitForCompletion();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WorkKeepsRunningWhenHandleIsDiscarded()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = WorkDefinition.Create("fire-and-forget", "Runs without retaining a handle.");
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
        {
            ran.SetResult();
            return Task.FromResult(WorkExecutionResult.Success());
        });

        await system.Start();

        _ = await system.Queue.Enqueue("fire-and-forget");

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkExecutionDoesNotFlowCallerExecutionContext()
    {
        var definition = WorkDefinition.Create("ambient-context", "Does not inherit caller AsyncLocal state.");
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(WorkOutput.FromValue(new AmbientContextResult(AmbientRequestValue.Value)))));

        await system.Start();

        try
        {
            AmbientRequestValue.Value = "request";
            var handle = await system.Queue.Enqueue("ambient-context");
            var completion = await handle.WaitForCompletion();

            Assert.True(completion.IsCompletedSuccessfully);
            Assert.Null(completion.Output?.ToValue<AmbientContextResult>()?.Value);
        }
        finally
        {
            AmbientRequestValue.Value = null;
        }
    }

    [Fact]
    public async Task UnknownDefinitionReturnsDeclarativeNotFoundOutcome()
    {
        var definition = WorkDefinition.Create("known", "Runs.");
        var system = CreateSystem(definition, SuccessfulWork);

        await system.Start();

        var handle = await system.Queue.Enqueue(WorkDefinitionId.New());
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.NotFound, handle.QueueOutcome.Status);
        Assert.Equal(WorkCompletionStatus.NotFound, completion.Status);
        Assert.Contains(handle.QueueOutcome.Messages, message => message.Severity == WorkMessageSeverity.Error);
    }

    [Fact]
    public void QueueApiDoesNotRouteByArgumentType()
    {
        var enqueueMethods = typeof(IWorkQueue)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(IWorkQueue.Enqueue))
            .ToList();

        Assert.All(enqueueMethods, method =>
        {
            var firstParameter = method.GetParameters().FirstOrDefault();
            Assert.NotNull(firstParameter);
            Assert.Contains(firstParameter.ParameterType, new[] { typeof(WorkDefinitionId), typeof(string) });
        });
        Assert.DoesNotContain(enqueueMethods, method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Type)));
        Assert.DoesNotContain(enqueueMethods, method => method.GetParameters().First().ParameterType.IsGenericParameter);
    }

    [Fact]
    public async Task ValidationFailureUsesStructuredMessagesWithoutExceptionControlFlow()
    {
        var definition = WorkDefinition.Create("validate", "Returns validation errors.");
        var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Failure([
                WorkMessage.Error("sample.validation", "The sample input is invalid.", "input")
            ])));

        await system.Start();

        var handle = await system.Queue.Enqueue("validate");
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Contains(completion.Messages, message =>
            message.Code == "sample.validation" &&
            message.Severity == WorkMessageSeverity.Error &&
            message.Target == "input");
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected the queue to accept a worker.");

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker to exist.");

    private sealed record EchoArgs(string Message);

    private sealed record AmbientContextResult(string? Value);
}
