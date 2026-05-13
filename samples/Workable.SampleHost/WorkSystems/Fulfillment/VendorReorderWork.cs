using Workable;

namespace Workable.SampleHost.Fulfillment;

public sealed record VendorReorderInput(
    string VendorId,
    IReadOnlyList<ReorderLineInput> Lines,
    bool Expedite = false);

public sealed record ReorderLineInput(
    string Sku,
    int RequestedUnits,
    decimal UnitCost);

public sealed record VendorReorderOutput(
    string PurchaseOrderNumber,
    int LineCount,
    decimal TotalCost,
    DateOnly ExpectedShipDate);

[WorkMetadata("procurement.reorder.submit", "Fulfillment:Procurement", "Submits a sample replenishment order to a vendor.")]
public sealed class VendorReorderWork : IWorkExecutor<VendorReorderInput, VendorReorderOutput>
{
    public Task<WorkExecutionResult<VendorReorderOutput>> Execute(
        IWorkExecutionContext context,
        VendorReorderInput input,
        CancellationToken cancellationToken)
    {
        var total = input.Lines.Sum(line => line.RequestedUnits * line.UnitCost);
        var leadDays = input.Expedite ? 3 : 10;

        return Task.FromResult(WorkExecutionResult<VendorReorderOutput>.Success(new VendorReorderOutput(
            $"PO-{input.VendorId}-{Random.Shared.Next(1000, 9999)}",
            input.Lines.Count,
            total,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(leadDays)))));
    }
}
