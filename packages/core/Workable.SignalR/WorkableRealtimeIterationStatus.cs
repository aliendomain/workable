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
/// Represents either one status item or a terminal replay gap on an iteration status SignalR stream.
/// </summary>
/// <param name="Kind"><c>status</c> for an item or <c>gap</c> for a terminal replay gap.</param>
/// <param name="Status">The status item when <paramref name="Kind"/> is <c>status</c>.</param>
/// <param name="Gap">The replay gap when <paramref name="Kind"/> is <c>gap</c>.</param>
public sealed record WorkableRealtimeIterationStatusMessage(
    string Kind,
    WorkableRealtimeIterationStatus? Status,
    WorkableRealtimeIterationStatusGap? Gap)
{
    /// <summary>Gets the message kind used for status items.</summary>
    public const string StatusKind = "status";

    /// <summary>Gets the message kind used for terminal replay gaps.</summary>
    public const string GapKind = "gap";

    internal static WorkableRealtimeIterationStatusMessage From(WorkIterationStatusItem item)
        => new(StatusKind, WorkableRealtimeIterationStatus.From(item), Gap: null);

    internal static WorkableRealtimeIterationStatusMessage From(WorkIterationStatusGapException exception)
        => new(GapKind, Status: null, WorkableRealtimeIterationStatusGap.From(exception));
}
