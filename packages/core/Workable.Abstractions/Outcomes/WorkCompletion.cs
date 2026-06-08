namespace Workable;
/// <summary>
/// Represents the final completion state of a worker using raw <see cref="WorkOutput"/>.
/// </summary>
/// <param name="Status">The completion status reached by the worker.</param>
/// <param name="Worker">The final worker snapshot, when one was available at completion time.</param>
/// <param name="Output">The retained raw output payload, when the worker produced one.</param>
/// <param name="Messages">The retained messages associated with the completed worker.</param>
public sealed record WorkCompletion(
    WorkCompletionStatus Status,
    WorkerSnapshot? Worker,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages)
{
    /// <summary>
    /// Gets a value indicating whether the worker completed successfully.
    /// </summary>
    public bool IsCompletedSuccessfully => this.Status == WorkCompletionStatus.Completed;

    /// <summary>
    /// Converts the completion to a typed output view by deserializing the retained raw output.
    /// </summary>
    /// <typeparam name="TOutput">The logical output type to deserialize from <see cref="Output"/>.</typeparam>
    /// <returns>A typed completion view over the same completion data.</returns>
    public WorkCompletion<TOutput> ToTyped<TOutput>()
        => new(
            this.Status,
            this.Worker,
            this.Output is null ? default : this.Output.ToValue<TOutput>(),
            this.Output,
            this.Messages);
}

/// <summary>
/// Represents the final completion state of a worker with typed output.
/// </summary>
/// <typeparam name="TOutput">The logical output type deserialized from the retained worker output.</typeparam>
/// <param name="Status">The completion status reached by the worker.</param>
/// <param name="Worker">The final worker snapshot, when one was available at completion time.</param>
/// <param name="Output">The typed output value, when the worker produced one and deserialization succeeded.</param>
/// <param name="RawOutput">The retained raw output payload.</param>
/// <param name="Messages">The retained messages associated with the completed worker.</param>
public sealed record WorkCompletion<TOutput>(
    WorkCompletionStatus Status,
    WorkerSnapshot? Worker,
    TOutput? Output,
    WorkOutput? RawOutput,
    IReadOnlyList<WorkMessage> Messages)
{
    /// <summary>
    /// Gets a value indicating whether the worker completed successfully.
    /// </summary>
    public bool IsCompletedSuccessfully => this.Status == WorkCompletionStatus.Completed;
}
