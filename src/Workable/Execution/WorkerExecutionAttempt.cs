namespace Workable;

internal sealed record WorkerExecutionAttempt(
    WorkExecutionResult? Result,
    WorkMessage? ExceptionFailureMessage,
    Exception? Exception = null,
    WorkExceptionClassification? ExceptionClassification = null,
    bool IsTransientDeclarativeFailure = false)
{
    public bool IsExceptionFailure => this.ExceptionFailureMessage is not null;

    public WorkExecutionResult RequiredResult
        => this.Result ?? throw new InvalidOperationException("Execution attempt did not include a result.");

    public WorkMessage RequiredExceptionFailureMessage
        => this.ExceptionFailureMessage ?? throw new InvalidOperationException("Execution attempt did not include an exception failure message.");

    public Exception RequiredException
        => this.Exception ?? throw new InvalidOperationException("Execution attempt did not include an exception.");

    public WorkExceptionClassification RequiredExceptionClassification
        => this.ExceptionClassification ?? throw new InvalidOperationException("Execution attempt did not include an exception classification.");

    public static WorkerExecutionAttempt Completed(WorkExecutionResult result)
        => new(result, ExceptionFailureMessage: null);

    public static WorkerExecutionAttempt DeclarativeTransientFailed(WorkExecutionResult result)
        => new(result, ExceptionFailureMessage: null, IsTransientDeclarativeFailure: true);

    public static WorkerExecutionAttempt ExceptionFailed(
        WorkMessage message,
        Exception exception,
        WorkExceptionClassification classification)
        => new(Result: null, message, exception, classification);
}
