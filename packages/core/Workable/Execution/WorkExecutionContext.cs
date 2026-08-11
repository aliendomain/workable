namespace Workable;
internal sealed class WorkExecutionContext(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    Func<WorkRequestContext?> CancellationRequestContextCallback,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    Func<WorkInterruptionReason?> InterruptionReasonCallback,
    IWorkProfiler Profile,
    IServiceProvider Services,
    IChildWorkQueueService ChildQueue,
    IWorkIterationStatusPublisher Status,
    Func<WorkIdentifier, bool> AddIdentifierCallback,
    Action<FailedWorkerAutoCancelOverride> ConfigureFailedWorkerAutoCancelCallback,
    Func<IWorkQueueDurabilityTransaction, CancellationToken, Task> CompleteDurablyCallback) : IWorkExecutionContext
{
    private const string RequestedFailureSourceMetadataKey = "failureSource";
    private const string RequestedFailureSourceMetadataValue = "executionContext";
    private int durableCompletionRecorded;
    private RequestedFailureState? requestedFailure;

    public WorkSystemId WorkSystemId { get; } = WorkSystemId;

    public string? WorkSystemName { get; } = WorkSystemName;

    public WorkerId WorkerId { get; } = WorkerId;

    public WorkDefinition Definition { get; } = Definition;

    public WorkRequestContext RequestContext { get; } = RequestContext;

    public WorkRequestContext? CancellationRequestContext => CancellationRequestContextCallback();

    public WorkerOptions Options { get; } = Options;

    public WorkConfiguration Configuration { get; } = Configuration;

    public bool IsInterrupted => this.InterruptionReason is not null;

    public WorkInterruptionReason? InterruptionReason => InterruptionReasonCallback();

    public IWorkProfiler Profile { get; } = Profile;

    public IServiceProvider Services { get; } = Services;

    internal IChildWorkQueueService ChildQueue { get; } = ChildQueue;

    public IWorkIterationStatusPublisher Status { get; } = Status;

    public bool AddIdentifier(WorkIdentifier identifier)
        => AddIdentifierCallback(identifier);

    internal WorkMessage? RequestedFailure => this.requestedFailure?.Message;

    internal bool IsRequestedFailureTransient => this.requestedFailure?.IsTransient == true;

    internal void RevokeChildExecution()
    {
        if (this.ChildQueue is ChildWorkQueueService childQueue)
        {
            childQueue.Revoke();
        }
    }

    public void Fail(string code, string message, string? target = null, bool transient = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        this.requestedFailure = new RequestedFailureState(
            new WorkMessage(
                code,
                WorkMessageSeverity.Error,
                message,
                target,
                new Dictionary<string, object?>
                {
                    [RequestedFailureSourceMetadataKey] = RequestedFailureSourceMetadataValue,
                }),
            transient);
    }

    public void RequireManualFailedWorkerHandling()
        => ConfigureFailedWorkerAutoCancelCallback(FailedWorkerAutoCancelOverride.Manual);

    public void AllowFailedWorkerAutoCancel(TimeSpan? autoCancelAfter = null)
    {
        if (this.Configuration.Recurrence.IsEnabled)
        {
            throw new InvalidOperationException("Failed-worker auto-cancel is not supported for recurring work.");
        }

        if (autoCancelAfter is { } delay && delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(autoCancelAfter), "Failed-worker auto-cancel delay must be greater than zero.");
        }

        ConfigureFailedWorkerAutoCancelCallback(
            autoCancelAfter is { } explicitDelay
                ? FailedWorkerAutoCancelOverride.Explicit(explicitDelay)
                : FailedWorkerAutoCancelOverride.Configured);
    }

    internal bool IsDurableCompletionRecorded
        => Volatile.Read(ref this.durableCompletionRecorded) == 1;

    public async Task CompleteDurably(
        IWorkQueueDurabilityTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await CompleteDurablyCallback(transaction, cancellationToken);
        Interlocked.Exchange(ref this.durableCompletionRecorded, 1);
    }

    private sealed record RequestedFailureState(WorkMessage Message, bool IsTransient);
}
