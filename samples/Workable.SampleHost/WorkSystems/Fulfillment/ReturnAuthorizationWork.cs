using Workable;

namespace Workable.SampleHost.Fulfillment;

public sealed record ReturnAuthorizationInput(
    string OrderId,
    string CustomerId,
    IReadOnlyList<ReturnLineInput> Lines,
    ReturnResolution Resolution);

public sealed record ReturnLineInput(
    string Sku,
    int Quantity,
    string ReasonCode);

public sealed record ReturnAuthorizationOutput(
    string RmaNumber,
    int LineCount,
    ReturnResolution Resolution,
    DateTimeOffset ExpiresAt);

public enum ReturnResolution
{
    Refund,
    Replacement,
    StoreCredit,
}

[WorkMetadata("returns.authorization.issue", "Fulfillment:Returns", "Issues a return authorization for one or more return lines.")]
public sealed class ReturnAuthorizationWork : IWorkExecutor<ReturnAuthorizationInput, ReturnAuthorizationOutput>
{
    public Task<WorkExecutionResult<ReturnAuthorizationOutput>> Execute(
        IWorkExecutionContext context,
        ReturnAuthorizationInput input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult<ReturnAuthorizationOutput>.Success(new ReturnAuthorizationOutput(
            $"RMA-{DateTimeOffset.UtcNow:yyyyMMdd}-{Random.Shared.Next(10000, 99999)}",
            input.Lines.Count,
            input.Resolution,
            DateTimeOffset.UtcNow.AddDays(30))));
}
