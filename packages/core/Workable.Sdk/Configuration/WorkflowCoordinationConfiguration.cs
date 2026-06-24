namespace Workable;

/// <summary>
/// Configures orchestration-level behavior for one workflow definition.
/// </summary>
public sealed record WorkflowCoordinationConfiguration
{
    /// <summary>
    /// Gets the default workflow coordination configuration with durability disabled.
    /// </summary>
    public static WorkflowCoordinationConfiguration Default { get; } = new();

    /// <summary>
    /// Gets a configuration that marks the workflow as durable.
    /// </summary>
    public static WorkflowCoordinationConfiguration Durable { get; } = new()
    {
        IsDurable = true,
    };

    /// <summary>
    /// Gets a value indicating whether the workflow should use durable orchestration state.
    /// </summary>
    public bool IsDurable { get; init; }
}
