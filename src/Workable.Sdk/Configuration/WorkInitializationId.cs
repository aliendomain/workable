namespace Workable;

internal readonly record struct WorkInitializationId(Guid Value)
{
    /// <summary>
    /// Creates a new unique initialization registration identifier.
    /// </summary>
    /// <returns>A new unique initialization identifier.</returns>
    public static WorkInitializationId New() => new(Guid.NewGuid());
}
