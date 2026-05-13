using Workable;

namespace Workable.SampleHost.Fulfillment;

public sealed record OrderPickListInput(
    string WaveId,
    IReadOnlyList<string> OrderIds,
    PickPriority Priority = PickPriority.Standard);

public sealed record OrderPickListOutput(
    string PickListId,
    int OrderCount,
    int EstimatedItemCount,
    DateTimeOffset CreatedAt);

public enum PickPriority
{
    Economy,
    Standard,
    Expedited,
}

[WorkMetadata("fulfillment.picklist.create", "Fulfillment:Picking", "Creates a warehouse pick list for a fulfillment wave.")]
public sealed class OrderPickListWork : IWorkExecutor<OrderPickListInput, OrderPickListOutput>
{
    public Task<WorkExecutionResult<OrderPickListOutput>> Execute(
        IWorkExecutionContext context,
        OrderPickListInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<OrderPickListOutput>.Success(
            new OrderPickListOutput(
                $"pick_{Guid.NewGuid():N}"[..17],
                input.OrderIds.Count,
                Math.Max(input.OrderIds.Count, input.OrderIds.Count * Random.Shared.Next(1, 5)),
                DateTimeOffset.UtcNow),
            [WorkMessage.Info("fulfillment.picklist.created", $"Created pick list for wave {input.WaveId}.")]));
}
