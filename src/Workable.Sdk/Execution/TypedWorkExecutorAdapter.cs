using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Workable;

internal sealed class TypedWorkExecutorAdapter(
    object executor,
    Type inputType,
    bool hasTypedOutput,
    MethodInfo executeMethod) : IWorkExecutor
{
    public async Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        object? typedInput;
        try
        {
            typedInput = string.IsNullOrWhiteSpace(input?.Json)
                ? null
                : input.ToValue(inputType);
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
