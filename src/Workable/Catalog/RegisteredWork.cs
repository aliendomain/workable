using System.Collections.Concurrent;

namespace Workable;

internal sealed record RegisteredWork(
    WorkDefinition Definition,
    Func<IServiceProvider, IWorkExecutor> ExecutorFactory,
    IReadOnlyList<WorkExceptionClassifier> ExceptionClassifiers,
    IReadOnlyList<WorkAutomaticStartRegistration> AutomaticStarts,
    IReadOnlyList<WorkInitializationRegistration> Initializers)
{
    private readonly ConcurrentDictionary<WorkInitializationId, LazyInitializationState> lazyInitializationStates = [];

    public RegisteredWork(
        WorkDefinition definition,
        Func<IServiceProvider, IWorkExecutor> executorFactory,
        IReadOnlyList<WorkExceptionClassifier> exceptionClassifiers)
        : this(definition, executorFactory, exceptionClassifiers, [], [])
    {
    }

    public async Task<WorkExecutionResult> RunLazyInitialization(
        WorkInitializationRegistration initializer,
        Func<Task<WorkExecutionResult>> initialize)
    {
        var state = this.lazyInitializationStates.GetOrAdd(initializer.Id, _ => new LazyInitializationState());
        await state.Sync.WaitAsync();
        try
        {
            if (state.IsComplete)
            {
                return WorkExecutionResult.Success();
            }

            var result = await initialize();
            if (!result.HasErrors)
            {
                state.IsComplete = true;
            }

            return result;
        }
        finally
        {
            state.Sync.Release();
        }
    }

    private sealed class LazyInitializationState
    {
        public SemaphoreSlim Sync { get; } = new(1, 1);

        public bool IsComplete { get; set; }
    }
}
