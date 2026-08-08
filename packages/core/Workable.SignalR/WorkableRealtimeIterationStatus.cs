using System.Text.Json;

namespace Workable;

/// <summary>
/// Represents one ordered work iteration status item delivered through a SignalR streaming invocation.
/// </summary>
/// <param name="OccurredAt">The time the status item was published.</param>
/// <param name="WorkSystemName">The optional configured system name.</param>
/// <param name="WorkerId">The worker that owns the iteration.</param>
/// <param name="IterationSequence">The iteration sequence within the worker.</param>
/// <param name="Sequence">The status item sequence within the iteration.</param>
/// <param name="WorkDefinitionName">The executing work definition.</param>
/// <param name="Type">The application-defined status type.</param>
/// <param name="Data">The optional structured payload.</param>
public sealed record WorkableRealtimeIterationStatus(
    DateTimeOffset OccurredAt,
    string? WorkSystemName,
    WorkerId WorkerId,
    long IterationSequence,
    long Sequence,
    string WorkDefinitionName,
    string Type,
    JsonElement? Data)
{
    internal static WorkableRealtimeIterationStatus From(WorkIterationStatusItem item)
        => new(
            item.OccurredAt,
            item.WorkSystemName,
            item.Iteration.WorkerId,
            item.Iteration.Sequence,
            item.Sequence,
            item.WorkDefinitionName,
            item.Type,
            item.Data);
}

/// <summary>
/// Describes a terminal replay gap for an iteration status SignalR stream.
/// </summary>
/// <param name="WorkerId">The worker that owns the iteration.</param>
/// <param name="IterationSequence">The iteration sequence within the worker.</param>
/// <param name="RequestedAfterSequence">The exclusive sequence cursor requested by the client.</param>
/// <param name="FirstAvailableSequence">The first status sequence still retained, or null when none remain.</param>
/// <param name="LastAvailableSequence">The last status sequence currently available, or null when none remain.</param>
public sealed record WorkableRealtimeIterationStatusGap(
    WorkerId WorkerId,
    long IterationSequence,
    long RequestedAfterSequence,
    long? FirstAvailableSequence,
    long? LastAvailableSequence)
{
    internal static WorkableRealtimeIterationStatusGap From(WorkIterationStatusGapException exception)
        => new(
            exception.Iteration.WorkerId,
            exception.Iteration.Sequence,
            exception.AfterSequence,
            exception.FirstAvailableSequence,
            exception.LastAvailableSequence);
}

/// <summary>
/// Represents the authoritative retained terminal state for one completed work iteration.
/// </summary>
/// <param name="WorkerId">The worker that owns the iteration.</param>
/// <param name="WorkerRevision">The worker revision observed when the terminal state was read.</param>
/// <param name="WorkerState">The worker state observed when the terminal state was read.</param>
/// <param name="IterationSequence">The completed iteration sequence.</param>
/// <param name="StartedAt">The time the iteration began executing.</param>
/// <param name="CompletedAt">The time the iteration reached its terminal status.</param>
/// <param name="ExecutionDuration">The retained execution duration.</param>
/// <param name="Status">The terminal completion status.</param>
/// <param name="AttemptCount">The retry attempt count within the iteration lineage.</param>
/// <param name="Output">The generic retained Workable output, when one exists.</param>
/// <param name="Messages">The retained completion messages.</param>
/// <param name="CancellationOrigin">The accepted cancellation request origin associated with the iteration, when available.</param>
public sealed record WorkableRealtimeIterationCompleted(
    WorkerId WorkerId,
    long WorkerRevision,
    WorkerState WorkerState,
    long IterationSequence,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan ExecutionDuration,
    WorkCompletionStatus Status,
    int AttemptCount,
    WorkOutput? Output,
    IReadOnlyList<WorkMessage> Messages,
    WorkOrigin? CancellationOrigin)
{
    internal static WorkableRealtimeIterationCompleted From(WorkIterationStatusCompletion completion)
        => new(
            completion.WorkerId,
            completion.WorkerRevision,
            completion.WorkerState,
            completion.Iteration.Sequence,
            completion.Iteration.StartedAt,
            completion.Iteration.CompletedAt,
            completion.Iteration.ExecutionDuration,
            completion.Iteration.Status,
            completion.Iteration.AttemptCount,
            completion.Iteration.Output,
            completion.Iteration.Messages,
            completion.CancellationOrigin);
}

/// <summary>
/// Represents one status item, an authoritative terminal result, or a terminal replay gap.
/// </summary>
/// <param name="Kind"><c>status</c>, <c>completed</c>, or <c>gap</c>.</param>
/// <param name="Status">The status item when <paramref name="Kind"/> is <c>status</c>.</param>
/// <param name="Gap">The replay gap when <paramref name="Kind"/> is <c>gap</c>.</param>
/// <param name="Completed">The retained terminal result when <paramref name="Kind"/> is <c>completed</c>.</param>
public sealed record WorkableRealtimeIterationStatusMessage(
    string Kind,
    WorkableRealtimeIterationStatus? Status,
    WorkableRealtimeIterationStatusGap? Gap,
    WorkableRealtimeIterationCompleted? Completed = null)
{
    /// <summary>Gets the message kind used for status items.</summary>
    public const string StatusKind = "status";

    /// <summary>Gets the message kind used for terminal replay gaps.</summary>
    public const string GapKind = "gap";

    /// <summary>Gets the message kind used for authoritative terminal results.</summary>
    public const string CompletedKind = "completed";

    internal static WorkableRealtimeIterationStatusMessage From(WorkIterationStatusItem item)
        => new(StatusKind, WorkableRealtimeIterationStatus.From(item), Gap: null);

    internal static WorkableRealtimeIterationStatusMessage From(WorkIterationStatusGapException exception)
        => new(GapKind, Status: null, WorkableRealtimeIterationStatusGap.From(exception));

    internal static WorkableRealtimeIterationStatusMessage From(WorkIterationStatusCompletion completion)
        => new(
            CompletedKind,
            Status: null,
            Gap: null,
            WorkableRealtimeIterationCompleted.From(completion));
}
