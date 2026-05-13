using Workable;

namespace Workable.SampleHost.Operations;

public sealed record InventoryAdjustInput(
    string Sku,
    int QuantityDelta,
    InventoryAdjustmentReason Reason,
    string WarehouseCode,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record InventoryAdjustOutput(
    string Sku,
    int QuantityDelta,
    int EstimatedQuantity,
    DateTimeOffset AppliedAt);

public enum InventoryAdjustmentReason
{
    CycleCount,
    Damage,
    Return,
    ManualCorrection,
}

[WorkMetadata("inventory.adjust", "Commerce:Inventory", "Applies an inventory adjustment with enum and dictionary input.")]
public sealed class InventoryAdjustWork : IWorkExecutor<InventoryAdjustInput, InventoryAdjustOutput>
{
    public Task<WorkExecutionResult<InventoryAdjustOutput>> Execute(
        IWorkExecutionContext context,
        InventoryAdjustInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<InventoryAdjustOutput>.Success(new InventoryAdjustOutput(
            input.Sku,
            input.QuantityDelta,
            Math.Max(0, 100 + input.QuantityDelta),
            DateTimeOffset.UtcNow)));
}
