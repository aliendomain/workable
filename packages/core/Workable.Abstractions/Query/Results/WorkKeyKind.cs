namespace Workable;

/// <summary>
/// Identifies which relationship-key slot a value came from.
/// </summary>
public enum WorkKeyKind
{
    /// <summary>
    /// The key comes from the primary subject identifier.
    /// </summary>
    Subject,

    /// <summary>
    /// The key comes from the concurrency grouping key.
    /// </summary>
    ConcurrencyKey,

    /// <summary>
    /// The key comes from an additional identifier.
    /// </summary>
    Identifier,
}
