using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
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

        var initializers = worker.Work.Initializers
            .OrderBy(initializer => initializer.ExecutionOrder ?? int.MaxValue)
            .ToList();
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
        if (initializer is IWorkInitializer untyped)
        {
            return await untyped.Initialize(context, cancellationToken);
        }

        return await this.RunTypedInitializer(registration, initializer, context, input, cancellationToken);
    }

    private async Task<WorkExecutionResult> RunTypedInitializer(
        WorkInitializationRegistration registration,
        object initializer,
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        var initializerInterface = registration.InitializerType
            .GetInterfaces()
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IWorkInitializer<>))
            .OrderByDescending(type => input?.ClrType == type.GetGenericArguments()[0].AssemblyQualifiedName)
            .FirstOrDefault();
        if (initializerInterface is null)
        {
            throw new InvalidOperationException(
                $"Initializer type '{registration.InitializerType.FullName}' must implement {nameof(IWorkInitializer)} or {nameof(IWorkInitializer<object>)}.");
        }

        var inputType = initializerInterface.GetGenericArguments()[0];
        object? typedInput;
        try
        {
            typedInput = string.IsNullOrWhiteSpace(input?.Json)
                ? null
                : input.ToValue(inputType);
        }
        catch (JsonException ex)
        {
            return WorkExecutionResult.Failure(
            [
                WorkMessage.Error(
                    "workable.initialization.input_invalid_json",
                    $"Work initialization input could not be deserialized as {inputType.FullName}. {ex.Message}",
                    "input"),
            ]);
        }

        if (typedInput is null)
        {
            return WorkExecutionResult.Failure(
            [
                WorkMessage.Error(
                    "workable.initialization.input_required",
                    $"Work initializer '{registration.InitializerType.FullName}' requires input of type '{inputType.FullName}'.",
                    "input"),
            ]);
        }

        var method = initializerInterface.GetMethod(nameof(IWorkInitializer<object>.Initialize))
            ?? throw new InvalidOperationException($"Initializer type '{registration.InitializerType.FullName}' does not expose an Initialize method.");

        try
        {
            var task = (Task<WorkExecutionResult>)(method.Invoke(initializer, [context, typedInput, cancellationToken])
                ?? throw new InvalidOperationException($"Initializer type '{registration.InitializerType.FullName}' returned null."));
            return await task;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
