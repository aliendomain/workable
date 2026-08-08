namespace Workable;

/// <summary>
/// Indicates that an iteration status subscription limit has been reached.
/// </summary>
public sealed class WorkIterationStatusSubscriptionLimitException : InvalidOperationException
{
    /// <summary>
    /// Creates an iteration status subscription-limit exception.
    /// </summary>
    public WorkIterationStatusSubscriptionLimitException(
        WorkerIterationReference iteration,
        int maximumSubscriptions,
        bool isSystemLimit)
        : base(isSystemLimit
            ? $"The work system has reached its limit of {maximumSubscriptions} active iteration status subscriptions."
            : $"Worker '{iteration.WorkerId}' iteration {iteration.Sequence} has reached its limit of " +
                $"{maximumSubscriptions} active status subscriptions.")
    {
        this.Iteration = iteration;
        this.MaximumSubscriptions = maximumSubscriptions;
        this.IsSystemLimit = isSystemLimit;
    }

    /// <summary>Gets the iteration requested by the subscriber.</summary>
    public WorkerIterationReference Iteration { get; }

    /// <summary>Gets the configured subscription limit.</summary>
    public int MaximumSubscriptions { get; }

    /// <summary>Gets whether the system-wide limit, rather than the per-iteration limit, was reached.</summary>
    public bool IsSystemLimit { get; }
}
