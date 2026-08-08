namespace Workable;

internal sealed class AuthorizedWorkQueueService(
    WorkSystemCatalog catalog,
    IWorkQueueService inner,
    WorkAuthorizationEvaluator authorization,
    WorkRequestContext requestContext,
    bool canViewDiagnostics) : IWorkQueueService
{
    public void NotifyDurableWorkAvailable()
        => inner.NotifyDurableWorkAvailable();

    public async Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGetWork(name, out var registeredWork))
        {
            return NotFound(name);
        }

        var decision = authorization.AuthorizeQueue(registeredWork, input, options, requestContext);
        if (decision.IsAllowed)
        {
            var handle = await inner.Enqueue(name, input, options, cancellationToken);
            return canViewDiagnostics ? handle : new ProfileFilteredWorkerHandle(handle);
        }

        return decision.IsInvalid
            ? Invalid(decision.Messages)
            : Rejected(name);
    }

    public async Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGetWork(name, out var registeredWork))
        {
            return NotFound(name);
        }

        var workInput = ToWorkInput(input);
        var decision = authorization.AuthorizeQueue(registeredWork, workInput, options, requestContext);
        if (decision.IsAllowed)
        {
            var handle = await inner.Enqueue(name, workInput, options, cancellationToken);
            return canViewDiagnostics ? handle : new ProfileFilteredWorkerHandle(handle);
        }

        return decision.IsInvalid
            ? Invalid(decision.Messages)
            : Rejected(name);
    }

    private static IWorkerHandle Rejected(string name)
        => WorkerHandle.Rejected(WorkQueueOutcome.Unauthorized(name));

    private static IWorkerHandle Invalid(IReadOnlyList<WorkMessage> messages)
        => WorkerHandle.Rejected(WorkQueueOutcome.Invalid(messages));

    private static IWorkerHandle NotFound(string name)
        => WorkerHandle.Rejected(WorkQueueOutcome.NotFound(name));

    private static WorkInput? ToWorkInput<TInput>(TInput input)
        => input switch
        {
            null => null,
            WorkInput workInput => workInput,
            _ => WorkInput.FromValue(input, WorkData.DefaultJsonOptions),
        };

    private sealed class ProfileFilteredWorkerHandle(IWorkerHandle inner) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome => inner.QueueOutcome;

        public WorkerId? WorkerId => inner.WorkerId;

        public async Task<WorkCompletion> WaitForCompletion(
            CancellationToken cancellationToken = default)
            => WorkProfileAccessFilter.Apply(
                await inner.WaitForCompletion(cancellationToken),
                canViewDiagnostics: false);

        public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(
            CancellationToken cancellationToken = default)
            => WorkProfileAccessFilter.Apply(
                await inner.WaitForCompletion<TOutput>(cancellationToken),
                canViewDiagnostics: false);
    }
}
