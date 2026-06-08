using System.Reflection;

namespace Workable;

internal static class WorkExecutorAdapterFactory
{
    /// <summary>
    /// Adapts a raw or typed executor instance to the untyped <see cref="IWorkExecutor"/> runtime contract.
    /// </summary>
    /// <param name="executor">The executor instance to adapt.</param>
    /// <returns>The adapted untyped executor.</returns>
    public static IWorkExecutor Create(object executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        if (executor is IWorkExecutor rawExecutor)
        {
            return rawExecutor;
        }

        var shape = GetTypedShape(executor.GetType())
            ?? throw new InvalidOperationException(
                $"Executor type '{executor.GetType().FullName}' must implement {nameof(IWorkExecutor)}, {nameof(IWorkExecutor<object>)} or {nameof(IWorkExecutor<object, object>)}.");

        return new TypedWorkExecutorAdapter(
            executor,
            shape.InputType,
            shape.OutputType is not null,
            shape.ExecuteMethod);
    }

    /// <summary>
    /// Throws when an executor type does not implement one of the supported raw or typed executor contracts.
    /// </summary>
    /// <param name="executorType">The executor type to validate.</param>
    public static void ThrowIfUnsupported(Type executorType)
    {
        ArgumentNullException.ThrowIfNull(executorType);

        if (typeof(IWorkExecutor).IsAssignableFrom(executorType) || GetTypedShape(executorType) is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Executor type '{executorType.FullName}' must implement {nameof(IWorkExecutor)}, {nameof(IWorkExecutor<object>)} or {nameof(IWorkExecutor<object, object>)}.");
    }

    /// <summary>
    /// Applies generated typed input and output schemas to a definition when the executor type exposes a typed contract.
    /// </summary>
    /// <param name="definition">The definition to augment.</param>
    /// <param name="executorType">The executor type that may expose a typed contract.</param>
    /// <returns>The definition with generated schemas applied when appropriate.</returns>
    public static WorkDefinition ApplyTypedSchemas(WorkDefinition definition, Type? executorType)
    {
        if (executorType is null)
        {
            return definition;
        }

        var shape = GetTypedShape(executorType);
        if (shape is null)
        {
            return definition;
        }

        return definition with
        {
            InputSchema = definition.InputSchema == WorkSchema.None
                ? WorkSchema.FromType(shape.InputType)
                : definition.InputSchema,
            OutputSchema = shape.OutputType is not null && definition.OutputSchema == WorkSchema.None
                ? WorkSchema.FromType(shape.OutputType)
                : definition.OutputSchema,
        };
    }

    /// <summary>
    /// Applies a generated typed input schema when the definition does not already provide one.
    /// </summary>
    /// <typeparam name="TInput">The typed input contract.</typeparam>
    /// <param name="definition">The definition to augment.</param>
    /// <returns>The definition with a generated input schema when appropriate.</returns>
    public static WorkDefinition ApplyTypedSchemas<TInput>(WorkDefinition definition)
        => definition with
        {
            InputSchema = definition.InputSchema == WorkSchema.None
                ? WorkSchema.FromType<TInput>()
                : definition.InputSchema,
        };

    /// <summary>
    /// Applies generated typed input and output schemas when the definition does not already provide them.
    /// </summary>
    /// <typeparam name="TInput">The typed input contract.</typeparam>
    /// <typeparam name="TOutput">The typed output contract.</typeparam>
    /// <param name="definition">The definition to augment.</param>
    /// <returns>The definition with generated schemas applied when appropriate.</returns>
    public static WorkDefinition ApplyTypedSchemas<TInput, TOutput>(WorkDefinition definition)
        => definition with
        {
            InputSchema = definition.InputSchema == WorkSchema.None
                ? WorkSchema.FromType<TInput>()
                : definition.InputSchema,
            OutputSchema = definition.OutputSchema == WorkSchema.None
                ? WorkSchema.FromType<TOutput>()
                : definition.OutputSchema,
        };

    private static TypedExecutorShape? GetTypedShape(Type executorType)
    {
        var typedOutputInterface = executorType.GetInterfaces()
            .SingleOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IWorkExecutor<,>));
        if (typedOutputInterface is not null)
        {
            var typeArguments = typedOutputInterface.GetGenericArguments();
            return new(
                typeArguments[0],
                typeArguments[1],
                GetExecuteMethod(typedOutputInterface));
        }

        var typedInputInterface = executorType.GetInterfaces()
            .SingleOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IWorkExecutor<>));
        if (typedInputInterface is not null)
        {
            return new(
                typedInputInterface.GetGenericArguments()[0],
                OutputType: null,
                GetExecuteMethod(typedInputInterface));
        }

        return null;
    }

    private static MethodInfo GetExecuteMethod(Type executorInterface)
        => executorInterface.GetMethod(nameof(IWorkExecutor.Execute))
            ?? throw new InvalidOperationException($"Typed executor interface '{executorInterface.FullName}' does not expose an Execute method.");

    private sealed record TypedExecutorShape(
        Type InputType,
        Type? OutputType,
        MethodInfo ExecuteMethod);
}
