namespace Workable;

/// <summary>
/// Controls how a request/response work dispatch should behave.
/// </summary>
/// <param name="Completion">Whether dispatch returns after acceptance or waits for terminal completion.</param>
/// <param name="WorkerOptions">Optional worker option overrides to apply to the dispatched worker.</param>
public sealed record WorkDispatchOptions(
    WorkDispatchCompletion Completion = WorkDispatchCompletion.WaitForCompletion,
    WorkerOptions? WorkerOptions = null);
