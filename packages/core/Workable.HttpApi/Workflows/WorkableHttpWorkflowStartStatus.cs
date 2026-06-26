namespace Workable;

/// <summary>
/// Describes the immediate workflow-start request result.
/// </summary>
public enum WorkableHttpWorkflowStartStatus
{
    Accepted,
    Invalid,
    Unauthorized,
    NotFound,
}
