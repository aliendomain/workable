using System.Collections.Frozen;

namespace Workable;

/// <summary>
/// Declares the work definitions that an executing parent may queue through its scoped child queue.
/// </summary>
public sealed record WorkChildExecutionConfiguration
{
    private static readonly IReadOnlySet<string> NoChildren =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the default configuration, which does not permit delegated child execution.
    /// </summary>
    public static WorkChildExecutionConfiguration Default { get; } = new();

    /// <summary>
    /// Gets the case-insensitive definition names the parent may execute as children.
    /// </summary>
    public IReadOnlySet<string> AllowedDefinitionNames { get; init; } = NoChildren;

    /// <summary>
    /// Determines whether the supplied definition is a declared child of the parent.
    /// </summary>
    /// <param name="definitionName">The target work definition name.</param>
    /// <returns><see langword="true"/> when delegated execution is declared.</returns>
    public bool Allows(string definitionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionName);
        return this.AllowedDefinitionNames.Contains(definitionName);
    }

    /// <summary>
    /// Returns a copy with the supplied definition names added to the child allowlist.
    /// </summary>
    /// <param name="definitionNames">The child definition names to add.</param>
    /// <returns>The updated configuration.</returns>
    public WorkChildExecutionConfiguration AllowAdditional(params string[] definitionNames)
    {
        ArgumentNullException.ThrowIfNull(definitionNames);
        if (definitionNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Child work definition names cannot be empty or whitespace.", nameof(definitionNames));
        }

        return this with
        {
            AllowedDefinitionNames = this.AllowedDefinitionNames
                .Concat(definitionNames)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    internal WorkChildExecutionConfiguration Snapshot()
    {
        if (this.AllowedDefinitionNames.Count == 0)
        {
            return ReferenceEquals(this, Default) ? this : Default;
        }

        if (this.AllowedDefinitionNames is FrozenSet<string>)
        {
            return this;
        }

        return this with
        {
            AllowedDefinitionNames = this.AllowedDefinitionNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        };
    }
}
