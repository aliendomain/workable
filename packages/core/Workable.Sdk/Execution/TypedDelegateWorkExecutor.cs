using System.Text.Json;

namespace Workable;

internal sealed class TypedDelegateWorkExecutor<TInput>(
    Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute) : IWorkExecutor
{
    /// <summary>
    /// Deserializes typed input and executes the registered delegate for the current worker iteration.
    /// </summary>
    /// <param name="context">The execution context for the current worker iteration.</param>
    /// <param name="input">The raw input payload supplied to the worker, when one exists.</param>
    /// <param name="cancellationToken">A token that cancels execution.</param>
    /// <returns>The execution result produced by the delegate or an input-validation failure.</returns>
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

    /// <summary>
    /// Attempts to deserialize the raw worker input to the delegate's typed input contract.
    /// </summary>
    /// <param name="input">The raw input payload supplied to the worker, when one exists.</param>
    /// <param name="typedInput">When this method returns <see langword="true"/>, receives the deserialized typed input.</param>
    /// <param name="failure">When this method returns <see langword="false"/>, receives the execution result describing the input failure.</param>
    /// <returns><see langword="true"/> when the input was successfully deserialized; otherwise <see langword="false"/>.</returns>
    internal static bool TryReadInput(
        WorkInput? input,
        out TInput? typedInput,
        out WorkExecutionResult failure)
    {
        if (string.IsNullOrWhiteSpace(input?.Json))
        {
            typedInput = default;
            failure = TypedWorkExecutorAdapter.CreateMissingInputFailure(typeof(TInput));
            return false;
        }

        try
        {
            typedInput = JsonSerializer.Deserialize<TInput>(input.Json, WorkData.DefaultJsonOptions);
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
    /// <summary>
    /// Deserializes typed input and executes the registered delegate for the current worker iteration.
    /// </summary>
    /// <param name="context">The execution context for the current worker iteration.</param>
    /// <param name="input">The raw input payload supplied to the worker, when one exists.</param>
    /// <param name="cancellationToken">A token that cancels execution.</param>
    /// <returns>The untyped execution result produced by the delegate or an input-validation failure.</returns>
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
