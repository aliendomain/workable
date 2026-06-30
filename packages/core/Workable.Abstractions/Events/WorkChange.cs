namespace Workable;

/// <summary>
/// Describes one latest-state change notification.
/// </summary>
public sealed record WorkChange
{
    /// <summary>
    /// Creates a change notification.
    /// </summary>
    /// <param name="sequence">The stream-owned monotonic sequence assigned to the change.</param>
    /// <param name="occurredAt">The time the change was observed.</param>
    /// <param name="key">The coalescing key for the changed state.</param>
    public WorkChange(long sequence, DateTimeOffset occurredAt, WorkChangeKey key)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Change sequence must be greater than zero.");
        }

        ArgumentNullException.ThrowIfNull(key);

        this.Sequence = sequence;
        this.OccurredAt = occurredAt;
        this.Key = key;
    }

    /// <summary>
    /// Gets the stream-owned monotonic sequence assigned to the change.
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    /// Gets the time the change was observed.
    /// </summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Gets the coalescing key for the changed state.
    /// </summary>
    public WorkChangeKey Key { get; }
}
