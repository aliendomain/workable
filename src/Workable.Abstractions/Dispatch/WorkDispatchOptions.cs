namespace Workable;

public sealed record WorkDispatchOptions(
    WorkDispatchCompletion Completion = WorkDispatchCompletion.WaitForCompletion,
    WorkerOptions? WorkerOptions = null);
