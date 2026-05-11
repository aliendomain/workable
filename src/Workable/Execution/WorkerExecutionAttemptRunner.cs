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
            try
            {
                return WorkerExecutionAttempt.Completed(await invoker.Execute(worker, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var classification = exceptionHandler.Classify(worker, ex);
                if (!allowTransientRetry ||
                    classification != WorkExceptionClassification.Transient ||
                    retryAttempts >= transientRetry.Count)
                {
                    exceptionHandler.LogFinalException(worker, ex, classification, retryAttempts);
                    return WorkerExecutionAttempt.ExceptionFailed(
                        exceptionHandler.CreateExceptionFailureMessage(ex, classification, retryAttempts));
                }

                retryAttempts++;
                var delay = TransientRetryWorkerExecutionStrategy.GetRetryDelay(transientRetry, retryAttempts);
                exceptionHandler.LogRetrying(worker, ex, retryAttempts, transientRetry.Count, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
