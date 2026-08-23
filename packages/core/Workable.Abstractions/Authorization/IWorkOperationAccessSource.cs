namespace Workable;

/// <summary>
/// Optionally exposes exact caller-scoped operation capabilities for transports that advertise individual actions.
/// </summary>
public interface IWorkOperationAccessSource
{
    /// <summary>
    /// Describes the exact worker and workflow operation categories available to the supplied caller.
    /// </summary>
    ValueTask<WorkOperationAccessSummary> DescribeOperationAccess(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default);
}
