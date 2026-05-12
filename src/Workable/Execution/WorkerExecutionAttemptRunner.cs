namespace Workable;

internal sealed class WorkerExecutionAttemptRunner(
    WorkerExecutionInvoker invoker,
    WorkerExecutionExceptionHandler exceptionHandler)
{
    public async Task<WorkerExecutionAttempt> Execute(
        WorkerRecord worker,
        bool allowTransientRetry,
        CancellationToken cancellationToken)
    {
        var retryAttempts = 0;
        var transientRetry = worker.Configuration.TransientRetry;

        while (true)
        {
            var execution = await CaptureExecution(invoker.Execute(worker, cancellationToken));
            if (execution.Result is { } result)
            {
                return WorkerExecutionAttempt.Completed(result);
            }

            if (execution.WasCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var exception = execution.Exception ?? new InvalidOperationException("Worker execution failed without an exception.");
            var classification = exceptionHandler.Classify(worker, exception);
            if (!allowTransientRetry ||
                classification != WorkExceptionClassification.Transient ||
                retryAttempts >= transientRetry.Count)
            {
                exceptionHandler.LogFinalException(worker, exception, classification, retryAttempts);
                return WorkerExecutionAttempt.ExceptionFailed(
                    exceptionHandler.CreateExceptionFailureMessage(exception, classification, retryAttempts));
            }

            retryAttempts++;
            var delay = TransientRetryWorkerExecutionStrategy.GetRetryDelay(transientRetry, retryAttempts);
            exceptionHandler.LogRetrying(worker, exception, retryAttempts, transientRetry.Count, delay);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static async Task<ExecutionCapture> CaptureExecution(Task<WorkExecutionResult> execution)
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

    private sealed record ExecutionCapture(
        WorkExecutionResult? Result,
        bool WasCanceled,
        Exception? Exception)
    {
        public static ExecutionCapture Completed(WorkExecutionResult result)
            => new(result, WasCanceled: false, Exception: null);

        public static ExecutionCapture Canceled()
            => new(Result: null, WasCanceled: true, Exception: null);

        public static ExecutionCapture Failed(Exception exception)
            => new(Result: null, WasCanceled: false, exception);
    }
}
