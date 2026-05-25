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
                ["exceptionMessage"] = exception.Message,
                ["exceptionStackTrace"] = exception.StackTrace,
                ["innerExceptions"] = CreateInnerExceptionMetadata(exception),
                ["exceptionClassification"] = classification.ToString(),
                ["isTransient"] = isTransient,
                ["transientRetryAttempts"] = retryAttempts,
            });
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> CreateInnerExceptionMetadata(Exception exception)
    {
        var innerExceptions = new List<IReadOnlyDictionary<string, object?>>();
        AppendInnerExceptionMetadata(exception, innerExceptions);
        return innerExceptions;
    }

    private static void AppendInnerExceptionMetadata(
        Exception exception,
        List<IReadOnlyDictionary<string, object?>> innerExceptions)
    {
        if (exception is AggregateException aggregateException)
        {
            foreach (var inner in aggregateException.Flatten().InnerExceptions)
            {
                innerExceptions.Add(CreateExceptionMetadata(inner));
                AppendInnerExceptionMetadata(inner, innerExceptions);
            }

            return;
        }

        if (exception.InnerException is not { } innerException)
        {
            return;
        }

        innerExceptions.Add(CreateExceptionMetadata(innerException));
        AppendInnerExceptionMetadata(innerException, innerExceptions);
    }

    private static IReadOnlyDictionary<string, object?> CreateExceptionMetadata(Exception exception)
        => new Dictionary<string, object?>
        {
            ["exceptionType"] = exception.GetType().FullName,
            ["exceptionMessage"] = exception.Message,
            ["exceptionStackTrace"] = exception.StackTrace,
        };
}
