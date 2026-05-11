namespace Workable;

internal sealed record WorkerExecutionAttempt(
    WorkExecutionResult? Result,
    WorkMessage? ExceptionFailureMessage)
{
    public bool IsExceptionFailure => this.ExceptionFailureMessage is not null;

    public WorkExecutionResult RequiredResult
        => this.Result ?? throw new InvalidOperationException("Execution attempt did not include a result.");

    public WorkMessage RequiredExceptionFailureMessage
        => this.ExceptionFailureMessage ?? throw new InvalidOperationException("Execution attempt did not include an exception failure message.");

    public static WorkerExecutionAttempt Completed(WorkExecutionResult result)
        => new(result, ExceptionFailureMessage: null);

    public static WorkerExecutionAttempt ExceptionFailed(WorkMessage message)
        => new(Result: null, message);
}
