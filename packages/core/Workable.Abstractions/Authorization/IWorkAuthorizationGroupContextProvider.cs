namespace Workable;

/// <summary>
/// Resolves authorization groups from the current invocation context when that context represents a requested actor.
/// </summary>
/// <remarks>
/// Invocation adapters use this contract to contribute ambient authorization data without replacing the
/// actor-based <see cref="IWorkAuthorizationGroupProvider"/> used by durable and background execution.
/// </remarks>
public interface IWorkAuthorizationGroupContextProvider
{
    /// <summary>
    /// Attempts to resolve groups from the current invocation context.
    /// </summary>
    /// <param name="actor">The actor whose groups are required.</param>
    /// <param name="systemName">The system name being authorized, or <see langword="null"/> for the default system.</param>
    /// <param name="cancellationToken">A token that cancels group resolution.</param>
    /// <returns>
    /// The resolved groups, including an empty set when the current actor has no groups; otherwise
    /// <see langword="null"/> when no applicable invocation context is available.
    /// </returns>
    ValueTask<IReadOnlySet<string>?> GetCurrentGroups(
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken = default);
}
