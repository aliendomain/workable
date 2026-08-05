namespace Workable;

/// <summary>
/// Configures system-wide worker profile capture limits.
/// </summary>
public sealed record WorkSystemProfilingConfiguration
{
    /// <summary>
    /// The default maximum number of automatic instrumentation nodes retained per profile.
    /// </summary>
    public const int DefaultMaximumAutomaticInstrumentationNodes = 500;

    /// <summary>
    /// Gets the default profiling configuration.
    /// </summary>
    public static WorkSystemProfilingConfiguration Default { get; } = new();

    /// <summary>
    /// Gets the maximum automatic SQL, HTTP, and extension instrumentation nodes captured per profile.
    /// Explicit application profile nodes are not counted against this limit.
    /// </summary>
    public int MaximumAutomaticInstrumentationNodes { get; init; } = DefaultMaximumAutomaticInstrumentationNodes;
}
