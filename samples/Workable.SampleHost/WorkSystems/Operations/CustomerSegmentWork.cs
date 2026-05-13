using Workable;

namespace Workable.SampleHost.Operations;

public sealed record CustomerSegmentInput(
    IReadOnlyList<string> CustomerIds,
    SegmentRule Rule,
    bool DryRun = true);

public sealed record SegmentRule(
    decimal MinimumLifetimeValue,
    DateOnly ActiveSince,
    IReadOnlyList<string> Tags);

public sealed record CustomerSegmentOutput(
    int EvaluatedCount,
    int MatchedCount,
    bool DryRun);

[WorkMetadata("crm.segment.refresh", "CRM:Segments", "Refreshes a customer segment using nested criteria and string arrays.")]
public sealed class CustomerSegmentWork : IWorkExecutor<CustomerSegmentInput, CustomerSegmentOutput>
{
    public Task<WorkExecutionResult<CustomerSegmentOutput>> Execute(
        IWorkExecutionContext context,
        CustomerSegmentInput input,
        CancellationToken cancellationToken)
    {
        var matched = input.CustomerIds.Count(id => id.GetHashCode(StringComparison.Ordinal) % 2 == 0);

        return Task.FromResult(WorkExecutionResult<CustomerSegmentOutput>.Success(new CustomerSegmentOutput(
            input.CustomerIds.Count,
            matched,
            input.DryRun)));
    }
}
