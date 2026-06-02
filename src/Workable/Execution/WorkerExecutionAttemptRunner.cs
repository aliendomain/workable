namespace Workable;

internal sealed class WorkerExecutionAttemptRunner(
    WorkerExecutionInvoker invoker,
    WorkerExecutionExceptionHandler exceptionHandler)
{
    public async Task<WorkerExecutionAttempt> Execute(
        WorkerRecord worker,
        int retryAttempts,
        CancellationToken cancellationToken)
    {
        var invocationTask = invoker.Execute(worker, cancellationToken);
        var execution = invocationTask.IsCompletedSuccessfully
            ? ExecutionCapture.Completed(invocationTask.Result)
            : await CaptureIncompleteExecution(invocationTask).ConfigureAwait(false);
        if (execution.InvocationResult is { } invocation)
        {
            return invocation.RequestedFailureIsTransient
                ? WorkerExecutionAttempt.DeclarativeTransientFailed(invocation.Result)
                : WorkerExecutionAttempt.Completed(invocation.Result);
        }

        if (execution.WasCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var exception = execution.Exception ?? new InvalidOperationException("Worker execution failed without an exception.");
        var classification = exceptionHandler.Classify(worker, exception);
        return WorkerExecutionAttempt.ExceptionFailed(
            WorkerExecutionExceptionHandler.CreateExceptionFailureMessage(exception, classification, retryAttempts),
            exception,
            classification);
    }

    public void LogRetrying(WorkerRecord worker, WorkerExecutionAttempt attempt, int retryAttempt, int retryCount, TimeSpan delay)
        => exceptionHandler.LogRetrying(worker, attempt.RequiredException, retryAttempt, retryCount, delay);

    public void LogFinalException(WorkerRecord worker, WorkerExecutionAttempt attempt, int retryAttempts)
        => exceptionHandler.LogFinalException(worker, attempt.RequiredException, attempt.RequiredExceptionClassification, retryAttempts);

    private static async Task<ExecutionCapture> CaptureIncompleteExecution(Task<WorkerExecutionInvocationResult> execution)
    {
        await Task.WhenAny(execution).ConfigureAwait(false);

        if (execution.IsCompletedSuccessfully)
        {
            return ExecutionCapture.Completed(execution.Result);
        }

        if (execution.IsCanceled)
        {
            return ExecutionCapture.Canceled();
        }

        return ExecutionCapture.Failed(GetExecutionException(execution));
    }

    private static Exception GetExecutionException(Task execution)
        => execution.Exception switch
        {
            { InnerExceptions.Count: 1 } exception => exception.InnerException!,
            { } exception => exception,
            _ => new InvalidOperationException("Worker execution task faulted without an exception."),
        };

    private readonly record struct ExecutionCapture(
        WorkerExecutionInvocationResult? InvocationResult,
        bool WasCanceled,
        Exception? Exception)
    {
        public static ExecutionCapture Completed(WorkerExecutionInvocationResult result)
            => new(result, WasCanceled: false, Exception: null);

        public static ExecutionCapture Canceled()
            => new(InvocationResult: null, WasCanceled: true, Exception: null);

        public static ExecutionCapture Failed(Exception exception)
            => new(InvocationResult: null, WasCanceled: false, exception);
    }
}
