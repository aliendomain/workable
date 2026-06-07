using Workable;

namespace Workable.Tests;

[Trait("Category", "Execution")]
public sealed class WorkerExecutionExceptionHandlerShould
{
    [Fact]
    public void ClassifyWithWorkLevelClassifiersBeforeSystemAndGlobalClassifiers()
    {
        var systemClassifierCalled = false;
        var globalClassifierCalled = false;
        var worker = CreateWorker(
            [_ => WorkExceptionClassification.NonTransient]);
        var handler = new WorkerExecutionExceptionHandler(
            new WorkExceptionClassifierChain(
                [
                    _ =>
                    {
                        systemClassifierCalled = true;
                        return WorkExceptionClassification.Transient;
                    },
                ],
                [
                    _ =>
                    {
                        globalClassifierCalled = true;
                        return WorkExceptionClassification.Transient;
                    },
                ],
                logger: null),
            logger: null);

        var classification = handler.Classify(worker, new InvalidOperationException("Boom."));

        Assert.Equal(WorkExceptionClassification.NonTransient, classification);
        Assert.False(systemClassifierCalled);
        Assert.False(globalClassifierCalled);
    }

    [Fact]
    public void CreateFailureMessagesWithNestedExceptionMetadata()
    {
        var nested = new ApplicationException("Nested failure.");
        var exception = new AggregateException(
            "Aggregate failure.",
            new InvalidOperationException("First failure.", nested),
            new ArgumentException("Second failure."));

        var message = WorkerExecutionExceptionHandler.CreateExceptionFailureMessage(
            exception,
            WorkExceptionClassification.Transient,
            retryAttempts: 2);

        Assert.Equal("workable.execution.exception", message.Code);
        Assert.Equal(WorkMessageSeverity.Error, message.Severity);
        Assert.Equal(exception.Message, message.Text);
        var metadata = message.Metadata ?? throw new InvalidOperationException("Expected failure metadata.");
        Assert.Equal(typeof(AggregateException).FullName, metadata["exceptionType"]);
        Assert.Equal(exception.Message, metadata["exceptionMessage"]);
        Assert.Equal(WorkExceptionClassification.Transient.ToString(), metadata["exceptionClassification"]);
        Assert.Equal(true, metadata["isTransient"]);
        Assert.Equal(2, metadata["transientRetryAttempts"]);
        var innerExceptions = Assert.IsAssignableFrom<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            metadata["innerExceptions"]);
        Assert.Collection(
            innerExceptions,
            inner => AssertInnerException<InvalidOperationException>(inner, "First failure."),
            inner => AssertInnerException<ApplicationException>(inner, "Nested failure."),
            inner => AssertInnerException<ArgumentException>(inner, "Second failure."));
    }

    private static WorkerRecord CreateWorker(IReadOnlyList<WorkExceptionClassifier> workClassifiers)
    {
        var definition = WorkDefinition.Create("exception.handler.work");
        var work = new RegisteredWork(definition, _ => new NoopExecutor(), workClassifiers);
        var now = DateTimeOffset.UtcNow;
        return new WorkerRecord(
            WorkerId.New(),
            work,
            input: null,
            WorkerOptions.Default,
            definition.Configuration,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            isStartDeferred: false,
            messages: [],
            now,
            now);
    }

    private static void AssertInnerException<TException>(
        IReadOnlyDictionary<string, object?> metadata,
        string message)
        where TException : Exception
    {
        Assert.Equal(typeof(TException).FullName, metadata["exceptionType"]);
        Assert.Equal(message, metadata["exceptionMessage"]);
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
