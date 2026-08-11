using System.Text.Json;

namespace Workable;
/// <summary>
/// Represents one emitted work event.
/// </summary>
public sealed record WorkEvent
{
    /// <summary>
    /// Initializes a work event using the original work-definition event shape.
    /// </summary>
    public WorkEvent(
        DateTimeOffset occurredAt,
        WorkSystemId workSystemId,
        string? workSystemName,
        WorkerId? workerId,
        WorkDefinitionId? workDefinitionId,
        string? workDefinitionName,
        WorkSubjectId? subjectId,
        WorkConcurrencyKey? concurrencyKey,
        IReadOnlySet<WorkIdentifier> identifiers,
        string eventType,
        JsonElement? data)
        : this(
            occurredAt,
            workSystemId,
            workSystemName,
            workerId,
            workDefinitionId,
            workDefinitionName,
            subjectId,
            concurrencyKey,
            identifiers,
            eventType,
            data,
            WorkEventDefinitionKind.Work,
            workflowDefinitionId: null)
    {
    }

    /// <summary>
    /// Creates a work event.
    /// </summary>
    /// <param name="occurredAt">The time the event occurred.</param>
    /// <param name="workSystemId">The identifier of the system that emitted the event.</param>
    /// <param name="workSystemName">The optional name of the system that emitted the event.</param>
    /// <param name="workerId">The optional worker id associated with the event.</param>
    /// <param name="workDefinitionId">The optional definition id associated with the event.</param>
    /// <param name="workDefinitionName">The optional definition name associated with the event.</param>
    /// <param name="subjectId">The optional primary business subject associated with the event.</param>
    /// <param name="concurrencyKey">The optional concurrency grouping key associated with the event.</param>
    /// <param name="identifiers">The additional searchable identifiers associated with the event.</param>
    /// <param name="eventType">The event-type name.</param>
    /// <param name="data">The optional structured event payload.</param>
    /// <param name="definitionKind">The namespace of the definition that produced the event.</param>
    /// <param name="workflowDefinitionId">The optional workflow definition id associated with a workflow event.</param>
    public WorkEvent(
        DateTimeOffset occurredAt,
        WorkSystemId workSystemId,
        string? workSystemName,
        WorkerId? workerId,
        WorkDefinitionId? workDefinitionId,
        string? workDefinitionName,
        WorkSubjectId? subjectId,
        WorkConcurrencyKey? concurrencyKey,
        IReadOnlySet<WorkIdentifier> identifiers,
        string eventType,
        JsonElement? data,
        WorkEventDefinitionKind definitionKind,
        WorkflowDefinitionId? workflowDefinitionId = null)
    {
        this.OccurredAt = occurredAt;
        this.WorkSystemId = workSystemId;
        this.WorkSystemName = workSystemName;
        this.WorkerId = workerId;
        this.WorkDefinitionId = workDefinitionId;
        this.WorkDefinitionName = workDefinitionName;
        this.SubjectId = subjectId;
        this.ConcurrencyKey = concurrencyKey;
        this.Identifiers = identifiers;
        this.EventType = eventType;
        this.Data = data;
        this.DefinitionKind = definitionKind;
        this.WorkflowDefinitionId = workflowDefinitionId;
        this.DefinitionScope = string.IsNullOrWhiteSpace(workDefinitionName)
            ? null
            : new WorkEventDefinitionScope(definitionKind, workDefinitionName);
    }

    /// <summary>
    /// Gets the time the event occurred.
    /// </summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Gets the identifier of the system that emitted the event.
    /// </summary>
    public WorkSystemId WorkSystemId { get; }

    /// <summary>
    /// Gets the optional name of the system that emitted the event.
    /// </summary>
    public string? WorkSystemName { get; }

    /// <summary>
    /// Gets the optional worker id associated with the event.
    /// </summary>
    public WorkerId? WorkerId { get; }

    /// <summary>
    /// Gets the optional definition id associated with the event.
    /// </summary>
    public WorkDefinitionId? WorkDefinitionId { get; }

    /// <summary>
    /// Gets the namespace of the definition that produced the event.
    /// </summary>
    public WorkEventDefinitionKind DefinitionKind { get; }

    /// <summary>
    /// Gets the optional workflow definition id associated with a workflow event.
    /// </summary>
    public WorkflowDefinitionId? WorkflowDefinitionId { get; }

    internal WorkEventDefinitionScope? DefinitionScope { get; }

    /// <summary>
    /// Gets the optional definition name associated with the event.
    /// </summary>
    public string? WorkDefinitionName { get; }

    /// <summary>
    /// Gets the optional primary business subject associated with the event.
    /// </summary>
    public WorkSubjectId? SubjectId { get; }

    /// <summary>
    /// Gets the optional concurrency grouping key associated with the event.
    /// </summary>
    public WorkConcurrencyKey? ConcurrencyKey { get; }

    /// <summary>
    /// Gets the additional searchable identifiers associated with the event.
    /// </summary>
    public IReadOnlySet<WorkIdentifier> Identifiers { get; }

    /// <summary>
    /// Gets the event-type name.
    /// </summary>
    public string EventType { get; }

    /// <summary>
    /// Gets the optional structured event payload.
    /// </summary>
    public JsonElement? Data { get; }

    /// <summary>
    /// Deserializes the event payload to the requested type.
    /// </summary>
    /// <typeparam name="T">The payload type to deserialize.</typeparam>
    /// <param name="options">Optional JSON serializer options. When omitted, Workable uses its event JSON options.</param>
    /// <returns>The deserialized payload, or the default value when the event has no payload.</returns>
    public T? DeserializeData<T>(JsonSerializerOptions? options = null)
        => this.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } data
            ? data.Deserialize<T>(options ?? WorkEventJson.Options)
            : default;
}
