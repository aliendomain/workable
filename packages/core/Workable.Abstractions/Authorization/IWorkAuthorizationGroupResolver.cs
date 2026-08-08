namespace Workable;

/// <summary>
/// Resolves the effective authorization groups for a Workable request context.
/// </summary>
public interface IWorkAuthorizationGroupResolver
{
    /// <summary>
    /// Resolves groups from a matching system-scoped snapshot, the current invocation context, or the configured actor provider.
    /// </summary>
    /// <param name="requestContext">The caller context whose groups are required.</param>
    /// <param name="systemName">The system name being authorized, or <see langword="null"/> for the default system.</param>
    /// <param name="cancellationToken">A token that cancels group resolution.</param>
    /// <returns>The normalized authorization groups for the caller.</returns>
    ValueTask<IReadOnlySet<string>> GetGroups(
        WorkRequestContext requestContext,
        string? systemName,
        CancellationToken cancellationToken = default);
}
