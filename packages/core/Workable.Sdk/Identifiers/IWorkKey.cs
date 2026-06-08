namespace Workable;

/// <summary>
/// Represents a typed relationship key used for subjects, concurrency keys, and identifiers.
/// </summary>
public interface IWorkKey
{
    /// <summary>
    /// Gets the caller-defined key type, such as <c>user</c>, <c>tenant</c>, or <c>invoice</c>.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Gets the key value within the <see cref="Type"/> namespace.
    /// </summary>
    string Value { get; }
}
