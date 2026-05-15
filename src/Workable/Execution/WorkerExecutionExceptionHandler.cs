using Microsoft.Extensions.Logging;

namespace Workable;

internal sealed class WorkerExecutionExceptionHandler(
    WorkExceptionClassifierChain exceptionClassifier,
    ILogger? logger)
{
    public WorkExceptionClassification Classify(WorkerRecord worker, Exception exception)
        => exceptionClassifier.Classify(worker.Work, exception);

    public void LogRetrying(WorkerRecord worker, Exception exception, int retryAttempt, int retryCount, TimeSpan delay)
    {
        logger?.LogWarning(
            exception,
            "Worker {WorkerId} for work definition {WorkDefinitionId} failed with a transient exception. Retry attempt {RetryAttempt} of {RetryCount} will start after {RetryDelay}.",
            worker.Id,
            worker.Work.Definition.Id,
            retryAttempt,
            retryCount,
            delay);
    }

    public void LogFinalException(WorkerRecord worker, Exception exception, WorkExceptionClassification classification, int retryAttempts)
    {
        logger?.LogError(
            exception,
            "Worker {WorkerId} for work definition {WorkDefinitionId} failed with an unhandled exception. Transient: {IsTransient}. Retry attempts: {RetryAttempts}.",
            worker.Id,
            worker.Work.Definition.Id,
            classification == WorkExceptionClassification.Transient,
            retryAttempts);
    }

    public static WorkMessage CreateExceptionFailureMessage(Exception exception, WorkExceptionClassification classification, int retryAttempts)
    {
        var isTransient = classification == WorkExceptionClassification.Transient;
        return new WorkMessage(
            "workable.execution.exception",
            WorkMessageSeverity.Error,
            exception.Message,
            "execution.exception",
            new Dictionary<string, object?>
            {
                ["exceptionType"] = exception.GetType().FullName,
                ["exceptionClassification"] = classification.ToString(),
                ["isTransient"] = isTransient,
                ["transientRetryAttempts"] = retryAttempts,
            });
    }
}
