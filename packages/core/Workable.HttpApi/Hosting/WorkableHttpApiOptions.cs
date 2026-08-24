namespace Workable;

/// <summary>
/// Configures the built-in Workable HTTP API adapter surface.
/// </summary>
public sealed class WorkableHttpApiOptions
{
    /// <summary>
    /// Gets or sets the optional top-level authorization groups required to reach the built-in Workable HTTP API
    /// route surface at all.
    /// </summary>
    /// <remarks>
    /// These groups are evaluated before the built-in HTTP surface checks the target system for administrator access.
    /// They apply only to routes mapped by <c>MapWorkableApi(...)</c>.
    /// Host-defined endpoints that use Workable directly are unaffected.
    /// Once at least one group is configured, every caller to every built-in <c>/workable</c> route must match at least
    /// one configured group before the request can reach any system-specific built-in surface checks.
    /// </remarks>
    public IReadOnlyList<string> SurfaceAccessGroups { get; set; } = [];
}
