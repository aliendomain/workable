using Workable;

namespace Workable.SampleHost.Fulfillment;

public sealed record FulfillmentExceptionInput(
    string ExceptionId,
    FulfillmentExceptionType Type,
    string Notes,
    bool Escalate);

public sealed record FulfillmentExceptionOutput(
    string CaseId,
    string AssignedQueue,
    DateTimeOffset RoutedAt);

public enum FulfillmentExceptionType
{
    AddressProblem,
    InventoryShortage,
    CarrierDelay,
    DamagedItem,
}

[WorkMetadata("fulfillment.exception.route", "Fulfillment:Exceptions", "Routes a fulfillment exception to the correct operations queue.")]
public sealed class FulfillmentExceptionWork : IWorkExecutor<FulfillmentExceptionInput, FulfillmentExceptionOutput>
{
    public Task<WorkExecutionResult<FulfillmentExceptionOutput>> Execute(
        IWorkExecutionContext context,
        FulfillmentExceptionInput input,
        CancellationToken cancellationToken)
    {
        var queue = input.Escalate
            ? "ops-escalations"
            : input.Type switch
            {
                FulfillmentExceptionType.AddressProblem => "address-resolution",
                FulfillmentExceptionType.InventoryShortage => "inventory-control",
                FulfillmentExceptionType.CarrierDelay => "carrier-support",
                _ => "warehouse-support",
            };

        return Task.FromResult(WorkExecutionResult<FulfillmentExceptionOutput>.Success(
            new FulfillmentExceptionOutput(
                $"case_{Guid.NewGuid():N}"[..18],
                queue,
                DateTimeOffset.UtcNow),
            [WorkMessage.Info("fulfillment.exception.routed", $"Exception {input.ExceptionId} routed to {queue}.")]));
    }
}
