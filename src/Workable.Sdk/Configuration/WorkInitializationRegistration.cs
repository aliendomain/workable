using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal sealed record WorkInitializationRegistration(
    WorkInitializationId Id,
    Type InitializerType,
    WorkInitializationTiming Timing,
    int? ExecutionOrder,
    Func<IServiceProvider, object> InitializerFactory,
    WorkInitializationInvoker Invoker)
{
    private static readonly MethodInfo InvokeTypedInitializerMethod =
        typeof(WorkInitializationRegistration).GetMethod(nameof(InvokeTypedInitializer), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The typed initializer invoker could not be found.");

    public bool IsTyped { get; } =
        InitializerType.GetInterfaces().Any(IsTypedInitializerInterface);

    public Task<WorkExecutionResult> Invoke(
        object initializer,
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => this.Invoker(initializer, context, input, cancellationToken);

    public static WorkInitializationRegistration Create<TInitializer>(
        WorkInitializationTiming timing,
        int? executionOrder)
        where TInitializer : class
    {
        var initializerType = typeof(TInitializer);
        var isTyped = initializerType.GetInterfaces().Any(IsTypedInitializerInterface);
        if (!typeof(IWorkInitializer).IsAssignableFrom(initializerType) && !isTyped)
        {
            throw new InvalidOperationException(
                $"Initializer type '{initializerType.FullName}' must implement {nameof(IWorkInitializer)} or {nameof(IWorkInitializer<object>)}.");
        }

        if (timing == WorkInitializationTiming.OnceLazy && isTyped)
        {
            throw new InvalidOperationException(
                $"Initializer type '{initializerType.FullName}' cannot use {nameof(WorkInitializationTiming.OnceLazy)} because typed initializers depend on worker input.");
        }

        return new WorkInitializationRegistration(
            WorkInitializationId.New(),
            initializerType,
            timing,
            executionOrder,
            serviceProvider => serviceProvider.GetRequiredService<TInitializer>(),
            CreateInvoker(initializerType));
    }

    public static IReadOnlyList<WorkInitializationRegistration> Order(
        IReadOnlyList<WorkInitializationRegistration> registrations)
        => registrations.Count <= 1
            ? registrations
            : [.. registrations.OrderBy(initializer => initializer.ExecutionOrder ?? int.MaxValue)];

    private static WorkInitializationInvoker CreateInvoker(Type initializerType)
    {
        if (typeof(IWorkInitializer).IsAssignableFrom(initializerType))
        {
            return static (initializer, context, _, cancellationToken) =>
                ((IWorkInitializer)initializer).Initialize(context, cancellationToken);
        }

        var typedBindings = initializerType
            .GetInterfaces()
            .Where(IsTypedInitializerInterface)
            .Select(CreateTypedBinding)
            .ToArray();
        if (typedBindings.Length == 0)
        {
            throw new InvalidOperationException(
                $"Initializer type '{initializerType.FullName}' must implement {nameof(IWorkInitializer)} or {nameof(IWorkInitializer<object>)}.");
        }

        return (initializer, context, input, cancellationToken) =>
        {
            var bindingIndex = Array.FindIndex(typedBindings, binding => binding.InputClrType == input?.ClrType);
            var binding = bindingIndex >= 0
                ? typedBindings[bindingIndex]
                : typedBindings[0];
            return binding.Invoke(initializerType, initializer, context, input, cancellationToken);
        };
    }

    private static WorkTypedInitializationBinding CreateTypedBinding(Type initializerInterface)
    {
        var inputType = initializerInterface.GetGenericArguments()[0];
        var invoker = (WorkTypedInitializationInvoker)Delegate.CreateDelegate(
            typeof(WorkTypedInitializationInvoker),
            InvokeTypedInitializerMethod.MakeGenericMethod(inputType));
        return new WorkTypedInitializationBinding(
            inputType.AssemblyQualifiedName,
            invoker);
    }

    private static async Task<WorkExecutionResult> InvokeTypedInitializer<TInput>(
        Type initializerType,
        object initializer,
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        object? typedInput;
        try
        {
            typedInput = string.IsNullOrWhiteSpace(input?.Json)
                ? null
                : input.ToValue(typeof(TInput));
        }
        catch (JsonException ex)
        {
            return WorkExecutionResult.Failure(
            [
                WorkMessage.Error(
                    "workable.initialization.input_invalid_json",
                    $"Work initialization input could not be deserialized as {typeof(TInput).FullName}. {ex.Message}",
                    "input"),
            ]);
        }

        if (typedInput is null)
        {
            return WorkExecutionResult.Failure(
            [
                WorkMessage.Error(
                    "workable.initialization.input_required",
                    $"Work initializer '{initializerType.FullName}' requires input of type '{typeof(TInput).FullName}'.",
                    "input"),
            ]);
        }

        return await ((IWorkInitializer<TInput>)initializer).Initialize(
            context,
            (TInput)typedInput,
            cancellationToken);
    }

    private static bool IsTypedInitializerInterface(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IWorkInitializer<>);
}

internal delegate Task<WorkExecutionResult> WorkInitializationInvoker(
    object initializer,
    IWorkExecutionContext context,
    WorkInput? input,
    CancellationToken cancellationToken);

internal delegate Task<WorkExecutionResult> WorkTypedInitializationInvoker(
    Type initializerType,
    object initializer,
    IWorkExecutionContext context,
    WorkInput? input,
    CancellationToken cancellationToken);

internal readonly record struct WorkTypedInitializationBinding(
    string? InputClrType,
    WorkTypedInitializationInvoker Invoke);
