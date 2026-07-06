using System.Linq.Expressions;

namespace Workable;

/// <summary>
/// Builds one workflow definition by composing built-in step shapes.
/// </summary>
public interface IWorkflowBuilder
{
    /// <summary>
     /// Adds a step that queues one existing Workable work definition.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <param name="workDefinition">The target Workable work definition.</param>
    /// <param name="input">Optional static work input payload.</param>
    /// <returns>The same builder so additional steps can be chained.</returns>
    IWorkflowBuilder DispatchWork(
        string stepName,
        WorkDefinition workDefinition,
        WorkInput? input = null);

    /// <summary>
    /// Adds a step that queues one existing Workable work definition and returns a typed reference to that step.
    /// </summary>
    /// <typeparam name="TOutput">The logical output type produced by the referenced step.</typeparam>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <param name="workDefinition">The target Workable work definition.</param>
    /// <param name="input">Optional static work input payload.</param>
    /// <returns>A typed reference that later workflow steps can use.</returns>
    WorkflowStepReference<TOutput> DispatchWork<TOutput>(
        string stepName,
        WorkDefinition workDefinition,
        WorkInput? input = null);

    /// <summary>
    /// Adds a step that queues one existing Workable work definition with the input supplied when the workflow run starts.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <param name="workDefinition">The target Workable work definition.</param>
    /// <returns>The same builder so additional steps can be chained.</returns>
    IWorkflowBuilder DispatchWorkFromWorkflowInput(
        string stepName,
        WorkDefinition workDefinition);

    /// <summary>
    /// Adds a step that queues one existing Workable work definition with the input supplied when the workflow run starts and returns a typed reference to that step.
    /// </summary>
    /// <typeparam name="TOutput">The logical output type produced by the referenced step.</typeparam>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <param name="workDefinition">The target Workable work definition.</param>
    /// <returns>A typed reference that later workflow steps can use.</returns>
    WorkflowStepReference<TOutput> DispatchWorkFromWorkflowInput<TOutput>(
        string stepName,
        WorkDefinition workDefinition);

    /// <summary>
    /// Adds a step that expands a prior output array into one child work dispatch per element.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <param name="sourceStep">The earlier step whose completed output should be expanded.</param>
    /// <param name="workDefinition">The target Workable work definition.</param>
    /// <param name="selector">A typed selector that resolves the source array within the completed output.</param>
    /// <returns>The same builder so additional steps can be chained.</returns>
    IWorkflowBuilder DispatchEach<TSourceOutput, TChildInput>(
        string stepName,
        WorkflowStepReference<TSourceOutput> sourceStep,
        WorkDefinition workDefinition,
        Expression<Func<TSourceOutput, IEnumerable<TChildInput>?>> selector);

    /// <summary>
    /// Adds a parallel step that contains child work-dispatch steps.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <param name="configure">Builds the child parallel branches.</param>
    /// <returns>The same builder so additional steps can be chained.</returns>
    IWorkflowBuilder RunParallel(
        string stepName,
        Action<IWorkflowParallelBuilder> configure);

    /// <summary>
    /// Adds a join step that waits for earlier parallel branches to settle.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <returns>The same builder so additional steps can be chained.</returns>
    IWorkflowBuilder Join(string stepName);
}
