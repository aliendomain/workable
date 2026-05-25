namespace Workable;
internal sealed class WorkExecutionContext(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    WorkDefinition Definition,
    WorkOrigin Origin,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    Func<WorkInterruptionReason?> InterruptionReasonCallback,
    IWorkProfiler Profile,
    IServiceProvider Services,
    Func<WorkIdentifier, bool> AddIdentifierCallback,
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

    public WorkOrigin Origin { get; } = Origin;

    public WorkerOptions Options { get; } = Options;

    public WorkConfiguration Configuration { get; } = Configuration;

    public bool IsInterrupted => this.InterruptionReason is not null;

    public WorkInterruptionReason? InterruptionReason => InterruptionReasonCallback();

    public IWorkProfiler Profile { get; } = Profile;

    public IServiceProvider Services { get; } = Services;

    public bool AddIdentifier(WorkIdentifier identifier)
        => AddIdentifierCallback(identifier);

    internal WorkMessage? RequestedFailure => this.requestedFailure?.Message;

    internal bool IsRequestedFailureTransient => this.requestedFailure?.IsTransient == true;

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
