using System.Text.Json;

namespace Workable;

internal sealed class TypedDelegateWorkExecutor<TInput>(
    Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute) : IWorkExecutor
{
    public async Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        if (!TryReadInput(input, out TInput? typedInput, out var failure))
        {
            return failure;
        }

        return await execute(context, typedInput!, cancellationToken);
    }

    internal static bool TryReadInput(
        WorkInput? input,
        out TInput? typedInput,
        out WorkExecutionResult failure)
    {
        try
        {
            typedInput = string.IsNullOrWhiteSpace(input?.Json)
                ? default
                : JsonSerializer.Deserialize<TInput>(input.Json, WorkData.DefaultJsonOptions);
            failure = WorkExecutionResult.Success();
            return true;
        }
        catch (JsonException ex)
        {
            typedInput = default;
            failure = WorkExecutionResult.Failure(
            [
                WorkMessage.Error(
                    "workable.input.invalid_json",
                    $"Work input could not be deserialized as {typeof(TInput).FullName}. {ex.Message}",
                    "input"),
            ]);
            return false;
        }
    }
}

internal sealed class TypedDelegateWorkExecutor<TInput, TOutput>(
    Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute) : IWorkExecutor
{
    public async Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        if (!TypedDelegateWorkExecutor<TInput>.TryReadInput(input, out var typedInput, out var failure))
        {
            return failure;
        }

        var result = await execute(context, typedInput!, cancellationToken);
        return ((IUntypedWorkExecutionResult)result).ToUntyped();
    }
}
