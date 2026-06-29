namespace Workable;

/// <summary>
/// Describes the state bucket represented by a <see cref="WorkChangeKey"/>.
/// </summary>
public enum WorkChangeKind
{
    /// <summary>
    /// System-wide state changed.
    /// </summary>
    System,

    /// <summary>
    /// Diagnostics state changed.
    /// </summary>
    Diagnostics,

    /// <summary>
    /// One worker changed.
    /// </summary>
    Worker,

    /// <summary>
    /// Work associated with one definition changed.
    /// </summary>
    Definition,

    /// <summary>
    /// Work associated with one subject changed.
    /// </summary>
    Subject,

    /// <summary>
    /// Work associated with one concurrency key changed.
    /// </summary>
    ConcurrencyKey,

    /// <summary>
    /// Work associated with one identifier changed.
    /// </summary>
    Identifier,
}
