using System.Diagnostics.CodeAnalysis;

namespace Workable;

/// <summary>
/// Exposes the named systems registered in the current host.
/// </summary>
public interface IWorkSystemRegistry
{
    /// <summary>
    /// Gets the default unnamed system.
    /// </summary>
    IWorkSystem Default { get; }

    /// <summary>
    /// Gets all registered systems, including the default unnamed system when present.
    /// </summary>
    IReadOnlyCollection<IWorkSystem> Systems { get; }

    /// <summary>
    /// Attempts to resolve a named system.
    /// </summary>
    /// <param name="name">The registered system name to resolve.</param>
    /// <param name="workSystem">When this method returns <see langword="true"/>, receives the resolved system.</param>
    /// <returns><see langword="true"/> when a system with the requested name exists; otherwise <see langword="false"/>.</returns>
    bool TryGet(string name, [NotNullWhen(true)] out IWorkSystem? workSystem);
}
