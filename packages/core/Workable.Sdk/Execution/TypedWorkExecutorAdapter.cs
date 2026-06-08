using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Workable;

internal sealed class TypedWorkExecutorAdapter(
    object executor,
    Type inputType,
    bool hasTypedOutput,
    MethodInfo executeMethod) : IWorkExecutor
{
    /// <summary>
    /// Deserializes typed input and invokes the typed executor instance for the current worker iteration.
    /// </summary>
    /// <param name="context">The execution context for the current worker iteration.</param>
    /// <param name="input">The raw input payload supplied to the worker, when one exists.</param>
    /// <param name="cancellationToken">A token that cancels execution.</param>
    /// <returns>The untyped execution result produced by the typed executor or an input-validation failure.</returns>
    public async Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input?.Json))
        {
            return CreateMissingInputFailure(inputType);
        }

        object? typedInput;
        try
        {
            typedInput = input.ToValue(inputType);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return WorkExecutionResult.Failure(
            [
                WorkMessage.Error(
                    "workable.input.invalid_json",
                    $"Work input could not be deserialized as {inputType.FullName}. {ex.Message}",
                    "input"),
            ]);
        }

        try
        {
            var task = (Task)(executeMethod.Invoke(executor, [context, typedInput, cancellationToken])
                ?? throw new InvalidOperationException($"The typed work executor '{executor.GetType().FullName}' returned null."));

            await task;
            var result = GetTaskResult(task);

            return hasTypedOutput
                ? ((IUntypedWorkExecutionResult)result).ToUntyped()
                : (WorkExecutionResult)result;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    /// <summary>
    /// Creates the standard failure result used when typed work is invoked without required input.
    /// </summary>
    /// <param name="inputType">The typed input contract that was required.</param>
    /// <returns>The execution result describing the missing-input failure.</returns>
    internal static WorkExecutionResult CreateMissingInputFailure(Type inputType)
        => WorkExecutionResult.Failure(
        [
            WorkMessage.Error(
                "workable.input.required",
                $"Work input is required for typed work '{inputType.FullName}'.",
                "input"),
        ]);

    private static object GetTaskResult(Task task)
    {
        var taskType = task.GetType();
        while (taskType is not null)
        {
            if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                return taskType.GetProperty(nameof(Task<object>.Result))?.GetValue(task)
                    ?? throw new InvalidOperationException("Typed work executor returned a null result.");
            }

            taskType = taskType.BaseType;
        }

        throw new InvalidOperationException("Typed work executor returned a non-generic task.");
    }
}
