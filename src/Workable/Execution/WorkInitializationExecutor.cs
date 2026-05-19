using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal sealed class WorkInitializationExecutor(IServiceProvider rootServices)
{
    public async Task<WorkExecutionResult> Initialize(
        WorkerRecord worker,
        Func<WorkerRecord, IServiceProvider, IWorkExecutionContext> createContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var initializers = worker.Work.OrderedInitializers;
        if (initializers.Count == 0)
        {
            return WorkExecutionResult.Success();
        }

        foreach (var initializer in initializers)
        {
            if (initializer.Timing == WorkInitializationTiming.OncePerWorker &&
                worker.IsInitializationComplete(initializer.Id))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            WorkExecutionResult result;
            try
            {
                result = initializer.Timing == WorkInitializationTiming.OnceLazy
                    ? await worker.Work.RunLazyInitialization(
                        initializer,
                        () => this.RunInitializerInNewScope(initializer, worker, createContext, cancellationToken))
                    : await this.RunInitializerInNewScope(initializer, worker, createContext, cancellationToken);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (result.HasErrors)
            {
                return result;
            }

            if (initializer.Timing == WorkInitializationTiming.OncePerWorker)
            {
                worker.MarkInitializationComplete(initializer.Id);
            }
        }

        return WorkExecutionResult.Success();
    }

    private async Task<WorkExecutionResult> RunInitializerInNewScope(
        WorkInitializationRegistration registration,
        WorkerRecord worker,
        Func<WorkerRecord, IServiceProvider, IWorkExecutionContext> createContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = rootServices.CreateAsyncScope();
        var context = createContext(worker, scope.ServiceProvider);
        return await this.RunInitializer(
            registration,
            context,
            scope.ServiceProvider,
            worker.Input,
            cancellationToken);
    }

    private async Task<WorkExecutionResult> RunInitializer(
        WorkInitializationRegistration registration,
        IWorkExecutionContext context,
        IServiceProvider services,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        var initializer = registration.InitializerFactory(services);
        return await registration.Invoke(initializer, context, input, cancellationToken);
    }
}
