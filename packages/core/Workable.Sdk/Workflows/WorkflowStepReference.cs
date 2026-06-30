namespace Workable;

/// <summary>
/// Represents one previously declared workflow step.
/// </summary>
public abstract record WorkflowStepReference
{
    /// <summary>
    /// Initializes one workflow step reference.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    protected WorkflowStepReference(string stepName)
    {
        this.StepName = string.IsNullOrWhiteSpace(stepName)
            ? throw new ArgumentException("Workflow step name cannot be blank.", nameof(stepName))
            : stepName;
    }

    /// <summary>
    /// Gets the stable workflow-local step name.
    /// </summary>
    public string StepName { get; }
}

/// <summary>
/// Represents one previously declared workflow step with a known output type.
/// </summary>
/// <typeparam name="TOutput">The logical output type produced by the referenced step.</typeparam>
public sealed record WorkflowStepReference<TOutput>(string StepName)
    : WorkflowStepReference(StepName);
