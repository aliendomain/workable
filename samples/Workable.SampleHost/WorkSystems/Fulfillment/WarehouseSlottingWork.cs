using Workable;

namespace Workable.SampleHost.Fulfillment;

public sealed record WarehouseSlottingInput(
    string Sku,
    int AverageDailyUnits,
    decimal CubicFeet,
    IReadOnlyList<string> EligibleZones);

public sealed record WarehouseSlottingOutput(
    string Sku,
    string RecommendedZone,
    string RecommendedBin,
    decimal VelocityScore);

[WorkMetadata("warehouse.slotting.recommend", "Fulfillment:Warehouse", "Recommends a warehouse zone and bin for a SKU.")]
public sealed class WarehouseSlottingWork : IWorkExecutor<WarehouseSlottingInput, WarehouseSlottingOutput>
{
    public Task<WorkExecutionResult<WarehouseSlottingOutput>> Execute(
        IWorkExecutionContext context,
        WarehouseSlottingInput input,
        CancellationToken cancellationToken)
    {
        var zone = input.EligibleZones.Count == 0
            ? "A"
            : input.EligibleZones[0];
        var score = Math.Round(input.AverageDailyUnits / Math.Max(input.CubicFeet, 0.1m), 2);

        return Task.FromResult(WorkExecutionResult<WarehouseSlottingOutput>.Success(new WarehouseSlottingOutput(
            input.Sku,
            zone,
            $"{zone}-{Random.Shared.Next(1, 99):D2}-{Random.Shared.Next(1, 20):D2}",
            score)));
    }
}
