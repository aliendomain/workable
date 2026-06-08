namespace Workable;

/// <summary>
/// Represents one structured message emitted during validation, queueing, execution, or control operations.
/// </summary>
/// <param name="Code">The stable machine-readable message code.</param>
/// <param name="Severity">The message severity.</param>
/// <param name="Text">The human-readable message text.</param>
/// <param name="Target">The optional field, property, or contract target associated with the message.</param>
/// <param name="Metadata">Optional structured metadata associated with the message.</param>
public sealed record WorkMessage(
    string Code,
    WorkMessageSeverity Severity,
    string Text,
    string? Target = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    /// <summary>
    /// Gets the time the message was created.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creates a trace-level message.
    /// </summary>
    /// <param name="code">The stable machine-readable message code.</param>
    /// <param name="text">The human-readable message text.</param>
    /// <param name="target">The optional field, property, or contract target associated with the message.</param>
    /// <returns>The created trace-level message.</returns>
    public static WorkMessage Trace(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Trace, text, target);

    /// <summary>
    /// Creates a debug-level message.
    /// </summary>
    /// <param name="code">The stable machine-readable message code.</param>
    /// <param name="text">The human-readable message text.</param>
    /// <param name="target">The optional field, property, or contract target associated with the message.</param>
    /// <returns>The created debug-level message.</returns>
    public static WorkMessage Debug(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Debug, text, target);

    /// <summary>
    /// Creates an information-level message.
    /// </summary>
    /// <param name="code">The stable machine-readable message code.</param>
    /// <param name="text">The human-readable message text.</param>
    /// <param name="target">The optional field, property, or contract target associated with the message.</param>
    /// <returns>The created information-level message.</returns>
    public static WorkMessage Information(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Information, text, target);

    /// <summary>
    /// Creates an information-level message.
    /// </summary>
    /// <param name="code">The stable machine-readable message code.</param>
    /// <param name="text">The human-readable message text.</param>
    /// <param name="target">The optional field, property, or contract target associated with the message.</param>
    /// <returns>The created information-level message.</returns>
    public static WorkMessage Info(string code, string text, string? target = null)
        => Information(code, text, target);

    /// <summary>
    /// Creates a warning-level message.
    /// </summary>
    /// <param name="code">The stable machine-readable message code.</param>
    /// <param name="text">The human-readable message text.</param>
    /// <param name="target">The optional field, property, or contract target associated with the message.</param>
    /// <returns>The created warning-level message.</returns>
    public static WorkMessage Warning(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Warning, text, target);

    /// <summary>
    /// Creates an error-level message.
    /// </summary>
    /// <param name="code">The stable machine-readable message code.</param>
    /// <param name="text">The human-readable message text.</param>
    /// <param name="target">The optional field, property, or contract target associated with the message.</param>
    /// <returns>The created error-level message.</returns>
    public static WorkMessage Error(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Error, text, target);

    /// <summary>
    /// Creates a critical-level message.
    /// </summary>
    /// <param name="code">The stable machine-readable message code.</param>
    /// <param name="text">The human-readable message text.</param>
    /// <param name="target">The optional field, property, or contract target associated with the message.</param>
    /// <returns>The created critical-level message.</returns>
    public static WorkMessage Critical(string code, string text, string? target = null)
        => new(code, WorkMessageSeverity.Critical, text, target);
}
