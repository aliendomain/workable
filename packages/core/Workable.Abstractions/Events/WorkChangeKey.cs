namespace Workable;

/// <summary>
/// Identifies the state bucket changed by a coalesced notification.
/// </summary>
public sealed record WorkChangeKey
{
    private const string SystemType = "system";
    private const string WorkerType = "worker";
    private const string DefinitionType = "definition";
    private const string DiagnosticsType = "diagnostics";
    private const string ActorType = "actor";

    /// <summary>
    /// Creates a change key.
    /// </summary>
    /// <param name="kind">The kind of state bucket changed.</param>
    /// <param name="type">The key type within the kind.</param>
    /// <param name="value">The key value within the type.</param>
    public WorkChangeKey(WorkChangeKind kind, string type, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        this.Kind = kind;
        this.Type = type;
        this.Value = value;
    }

    /// <summary>
    /// Gets the kind of state bucket changed.
    /// </summary>
    public WorkChangeKind Kind { get; }

    /// <summary>
    /// Gets the key type within the kind.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets the key value within the type.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a system-wide change key.
    /// </summary>
    /// <returns>The system-wide change key.</returns>
    public static WorkChangeKey System()
        => new(WorkChangeKind.System, SystemType, SystemType);

    /// <summary>
    /// Creates a diagnostics-area change key.
    /// </summary>
    /// <param name="area">The diagnostics area that changed.</param>
    /// <returns>The diagnostics-area change key.</returns>
    public static WorkChangeKey Diagnostics(string area)
        => new(WorkChangeKind.Diagnostics, DiagnosticsType, Normalize(area));

    /// <summary>
    /// Creates a worker change key.
    /// </summary>
    /// <param name="workerId">The worker that changed.</param>
    /// <returns>The worker change key.</returns>
    public static WorkChangeKey Worker(WorkerId workerId)
        => new(WorkChangeKind.Worker, WorkerType, workerId.Value.ToString("N"));

    /// <summary>
    /// Creates a definition change key.
    /// </summary>
    /// <param name="definitionName">The definition whose state changed.</param>
    /// <returns>The definition change key.</returns>
    public static WorkChangeKey Definition(string definitionName)
        => new(WorkChangeKind.Definition, DefinitionType, Normalize(definitionName));

    /// <summary>
    /// Creates a subject change key.
    /// </summary>
    /// <param name="subjectId">The subject whose worker state changed.</param>
    /// <returns>The subject change key.</returns>
    public static WorkChangeKey Subject(WorkSubjectId subjectId)
        => new(WorkChangeKind.Subject, subjectId.Type, subjectId.Value);

    /// <summary>
    /// Creates a concurrency-key change key.
    /// </summary>
    /// <param name="concurrencyKey">The concurrency key whose worker state changed.</param>
    /// <returns>The concurrency-key change key.</returns>
    public static WorkChangeKey Concurrency(WorkConcurrencyKey concurrencyKey)
        => new(WorkChangeKind.ConcurrencyKey, concurrencyKey.Type, concurrencyKey.Value);

    /// <summary>
    /// Creates an identifier change key.
    /// </summary>
    /// <param name="identifier">The identifier whose worker state changed.</param>
    /// <returns>The identifier change key.</returns>
    public static WorkChangeKey Identifier(WorkIdentifier identifier)
        => new(WorkChangeKind.Identifier, identifier.Type, identifier.Value);

    /// <summary>
    /// Creates a change key for work originated by one actor.
    /// </summary>
    /// <param name="actorId">The stable actor identifier.</param>
    /// <returns>The actor-scoped change key.</returns>
    public static WorkChangeKey Actor(string actorId)
        => new(WorkChangeKind.Actor, ActorType, Normalize(actorId));

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
