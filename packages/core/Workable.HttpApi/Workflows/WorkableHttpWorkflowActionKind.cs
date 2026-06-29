namespace Workable;

/// <summary>
/// Identifies the supported workflow actions exposed by the HTTP API.
/// </summary>
public enum WorkableHttpWorkflowActionKind
{
    Start,
    Pause,
    Cancel,
}
