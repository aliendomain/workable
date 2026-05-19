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
    private int durableCompletionRecorded;

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
}
