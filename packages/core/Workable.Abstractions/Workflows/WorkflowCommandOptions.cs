namespace Workable;

/// <summary>
/// Controls how a workflow command should behave.
/// </summary>
/// <param name="Completion">Whether a start command returns after acceptance or waits for terminal completion.</param>
public sealed record WorkflowCommandOptions(
    WorkDispatchCompletion Completion = WorkDispatchCompletion.WaitForCompletion);
