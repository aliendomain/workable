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

        if (!authorization.CanDiscover(registeredWork.Definition))
        {
            return NotFound(name);
        }

        var decision = authorization.AuthorizeQueue(registeredWork, input, options, requestContext);
        if (decision.IsAllowed)
        {
            var handle = await inner.Enqueue(name, input, options, cancellationToken);
            var canRead = authorization.CanRead(registeredWork.Definition);
            return canRead && canViewDiagnostics
                ? handle
                : new AuthorizedWorkerHandle(handle, canRead, canViewDiagnostics);
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

        if (!authorization.CanDiscover(registeredWork.Definition))
        {
            return NotFound(name);
        }

        var workInput = ToWorkInput(input);
        var decision = authorization.AuthorizeQueue(registeredWork, workInput, options, requestContext);
        if (decision.IsAllowed)
        {
            var handle = await inner.Enqueue(name, workInput, options, cancellationToken);
            var canRead = authorization.CanRead(registeredWork.Definition);
            return canRead && canViewDiagnostics
                ? handle
                : new AuthorizedWorkerHandle(handle, canRead, canViewDiagnostics);
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

    private sealed class AuthorizedWorkerHandle(
        IWorkerHandle inner,
        bool canRead,
        bool canViewDiagnostics) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = WorkMessageAccessFilter.Apply(
            inner.QueueOutcome,
            canRead || canViewDiagnostics);

        public WorkerId? WorkerId => inner.WorkerId;

        public async Task<WorkCompletion> WaitForCompletion(
            CancellationToken cancellationToken = default)
        {
            var completion = WorkProfileAccessFilter.Apply(
                await inner.WaitForCompletion(cancellationToken),
                canViewDiagnostics);
            completion = completion with
            {
                Messages = WorkMessageAccessFilter.Apply(
                    completion.Messages,
                    canRead || canViewDiagnostics),
            };
            return canRead
                ? completion
                : completion with
                {
                    Worker = null,
                    Output = null,
                };
        }

        public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(
            CancellationToken cancellationToken = default)
        {
            if (!canRead)
            {
                var untypedCompletion = WorkProfileAccessFilter.Apply(
                    await inner.WaitForCompletion(cancellationToken),
                    canViewDiagnostics);
                return new WorkCompletion<TOutput>(
                    untypedCompletion.Status,
                    Worker: null,
                    Output: default,
                    RawOutput: null,
                    Messages: WorkMessageAccessFilter.Apply(
                        untypedCompletion.Messages,
                        canViewDiagnostics));
            }

            var completion = WorkProfileAccessFilter.Apply(
                await inner.WaitForCompletion<TOutput>(cancellationToken),
                canViewDiagnostics);
            completion = completion with
            {
                Messages = WorkMessageAccessFilter.Apply(
                    completion.Messages,
                    canRead || canViewDiagnostics),
            };
            return completion;
        }
    }
}
