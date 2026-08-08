namespace Workable;

/// <summary>
/// Indicates that a requested iteration status cursor has fallen behind the retained replay window.
/// </summary>
public sealed class WorkIterationStatusGapException : InvalidOperationException
{
    /// <summary>
    /// Creates an iteration status replay-gap exception.
    /// </summary>
    public WorkIterationStatusGapException(
        WorkerIterationReference iteration,
        long afterSequence,
        long firstAvailableSequence)
        : this(iteration, afterSequence, firstAvailableSequence, firstAvailableSequence)
    {
    }

    /// <summary>
    /// Creates an iteration status replay-gap exception with the complete available replay range.
    /// </summary>
    public WorkIterationStatusGapException(
        WorkerIterationReference iteration,
        long afterSequence,
        long firstAvailableSequence,
        long lastAvailableSequence)
        : this(iteration, afterSequence, (long?)firstAvailableSequence, lastAvailableSequence)
    {
    }

    /// <summary>
    /// Creates an iteration status replay-gap exception whose replay window may currently be empty.
    /// </summary>
    public WorkIterationStatusGapException(
        WorkerIterationReference iteration,
        long afterSequence,
        long? firstAvailableSequence,
        long? lastAvailableSequence)
        : base(CreateMessage(iteration, afterSequence, firstAvailableSequence, lastAvailableSequence))
    {
        if (firstAvailableSequence.HasValue != lastAvailableSequence.HasValue)
        {
            throw new ArgumentException("The first and last available sequences must either both be supplied or both be null.");
        }

        if (lastAvailableSequence < firstAvailableSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastAvailableSequence),
                "The last available sequence cannot precede the first available sequence.");
        }

        this.Iteration = iteration;
        this.AfterSequence = afterSequence;
        this.FirstAvailableSequence = firstAvailableSequence;
        this.LastAvailableSequence = lastAvailableSequence;
    }

    /// <summary>Gets the affected iteration.</summary>
    public WorkerIterationReference Iteration { get; }

    /// <summary>Gets the exclusive sequence cursor requested by the subscriber.</summary>
    public long AfterSequence { get; }

    /// <summary>Gets the first item sequence still retained.</summary>
    public long? FirstAvailableSequence { get; }

    /// <summary>Gets the last item sequence currently available.</summary>
    public long? LastAvailableSequence { get; }

    private static string CreateMessage(
        WorkerIterationReference iteration,
        long afterSequence,
        long? firstAvailableSequence,
        long? lastAvailableSequence)
        => firstAvailableSequence is { } first && lastAvailableSequence is { } last
            ? $"Iteration status items after sequence {afterSequence} are no longer fully available for worker " +
                $"'{iteration.WorkerId}' iteration {iteration.Sequence}. The available sequence range is " +
                $"{first} through {last}."
            : $"Iteration status items after sequence {afterSequence} are no longer available for worker " +
                $"'{iteration.WorkerId}' iteration {iteration.Sequence}. No status items are currently retained.";
}
