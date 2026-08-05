namespace Workable;

/// <summary>
/// Builds the capability snapshot for a hosted Workable system.
/// </summary>
public sealed class WorkSystemCapabilitiesBuilder
{
    /// <summary>
    /// Gets or sets a value indicating whether persistent coordination is available.
    /// </summary>
    public bool PersistentCoordinationAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether SQL profiling is available.
    /// </summary>
    public bool SqlProfilingAvailable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether outbound HTTP client profiling is available.
    /// </summary>
    public bool HttpClientProfilingAvailable { get; set; }

    /// <summary>
    /// Creates an immutable snapshot from the current capability values.
    /// </summary>
    /// <returns>The immutable system capability snapshot.</returns>
    public WorkSystemCapabilities Build()
        => new(this.PersistentCoordinationAvailable, this.SqlProfilingAvailable)
        {
            HttpClientProfilingAvailable = this.HttpClientProfilingAvailable,
        };
}
