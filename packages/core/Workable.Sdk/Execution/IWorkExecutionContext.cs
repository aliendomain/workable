namespace Workable;

/// <summary>
/// Provides the services and worker state visible to an executing work iteration.
/// </summary>
public interface IWorkExecutionContext
{
    /// <summary>
    /// Gets the identifier of the owning system.
    /// </summary>
    WorkSystemId WorkSystemId { get; }

    /// <summary>
    /// Gets the configured name of the owning system, or <see langword="null"/> for the default unnamed system.
    /// </summary>
    string? WorkSystemName { get; }

    /// <summary>
    /// Gets the identifier of the executing worker.
    /// </summary>
    WorkerId WorkerId { get; }

    /// <summary>
    /// Gets the resolved work definition being executed.
    /// </summary>
    WorkDefinition Definition { get; }

    /// <summary>
    /// Gets the durable caller context recorded when the worker was created.
    /// </summary>
    WorkRequestContext RequestContext { get; }

    /// <summary>
    /// Gets the caller context for the accepted cancellation request stopping this execution, when available.
    /// </summary>
    /// <remarks>
    /// This value is separate from <see cref="RequestContext"/>, which describes the request that created the worker.
    /// It is <see langword="null"/> before cancellation is requested and when execution stops for another reason.
    /// </remarks>
    WorkRequestContext? CancellationRequestContext => null;

    /// <summary>
    /// Gets the effective worker options used for the current worker.
    /// </summary>
    WorkerOptions Options { get; }

    /// <summary>
    /// Gets the effective runtime configuration used for the current worker.
    /// </summary>
    WorkConfiguration Configuration { get; }

    /// <summary>
    /// Gets a value indicating whether interruption has been requested for the current worker.
    /// </summary>
    bool IsInterrupted { get; }

    /// <summary>
    /// Gets the interruption reason when interruption has been requested.
    /// </summary>
    WorkInterruptionReason? InterruptionReason { get; }

    /// <summary>
    /// Gets the active worker profiler facade for the current execution scope.
    /// </summary>
    IWorkProfiler Profile { get; }

    /// <summary>
    /// Gets the scoped service provider for the current execution scope.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Adds a discovered identifier to the worker.
    /// </summary>
    /// <param name="identifier">The identifier to attach.</param>
    /// <returns><see langword="true"/> when the identifier was newly attached; otherwise <see langword="false"/>.</returns>
    bool AddIdentifier(WorkIdentifier identifier);

    /// <summary>
    /// Fails the current execution with a structured error message.
    /// </summary>
    /// <param name="code">The stable machine-readable failure code.</param>
    /// <param name="message">The human-readable failure message.</param>
    /// <param name="target">The optional field, property, or contract target associated with the failure.</param>
    /// <param name="transient"><see langword="true"/> to mark the failure as transient so transient retry may treat it as retryable; otherwise <see langword="false"/>.</param>
    void Fail(string code, string message, string? target = null, bool transient = false);

    /// <summary>
    /// Forces the current worker to remain in <c>Failed</c> for manual handling if this execution fails.
    /// </summary>
    void RequireManualFailedWorkerHandling();

    /// <summary>
    /// Allows the current worker to be auto-canceled if this execution fails.
    /// </summary>
    /// <param name="autoCancelAfter">
    /// Optional failed-state delay override. When omitted, Workable uses the worker's configured failed-worker
    /// auto-cancel delay.
    /// </param>
    void AllowFailedWorkerAutoCancel(TimeSpan? autoCancelAfter = null);

    /// <summary>
    /// Completes the worker durably within a store-specific transaction.
    /// </summary>
    /// <param name="transaction">The store-specific durability transaction to complete within.</param>
    /// <param name="cancellationToken">A token that cancels the durable completion operation.</param>
    Task CompleteDurably(
        IWorkQueueDurabilityTransaction transaction,
        CancellationToken cancellationToken = default);
}
