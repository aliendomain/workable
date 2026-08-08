using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Resolves authorization groups from the current HTTP user's claims.
/// </summary>
public sealed class HttpContextClaimsWorkAuthorizationGroupProvider(
    IHttpContextAccessor httpContextAccessor,
    IWorkActorFactory actors,
    IOptions<WorkableAspNetCoreAuthorizationOptions> options) : IWorkAuthorizationGroupContextProvider
{
    private static readonly object GroupsCacheKey = new();
    private const string DefaultSystemCacheKey = "<default>";

    /// <summary>
    /// Gets the group values associated with the current authenticated HTTP user.
    /// </summary>
    /// <param name="actor">The actor being authorized.</param>
    /// <param name="systemName">The system name being authorized, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="cancellationToken">A token that cancels group resolution.</param>
    /// <returns>The resolved group values for the current authenticated user.</returns>
    public ValueTask<IReadOnlySet<string>?> GetCurrentGroups(
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var httpContext = httpContextAccessor.HttpContext;
        var cacheKey = string.IsNullOrWhiteSpace(systemName) ? DefaultSystemCacheKey : systemName;
        if (httpContext is null || httpContext.User.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(null);
        }

        var user = httpContext.User;
        if (actors.Create(user) != actor)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(null);
        }

        Dictionary<string, IReadOnlySet<string>>? cache = null;
        if (httpContext.Items[GroupsCacheKey] is Dictionary<string, IReadOnlySet<string>> existingCache)
        {
            cache = existingCache;
        }

        if (cache is not null && cache.TryGetValue(cacheKey, out var cachedGroups))
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(cachedGroups);
        }

        var groups = user.Claims
            .Where(claim => options.Value.GroupClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .SelectMany(claim => SplitGroups(claim.Value, options.Value.GroupClaimValueSeparators))
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        cache ??= new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        cache[cacheKey] = groups;
        httpContext.Items[GroupsCacheKey] = cache;

        return ValueTask.FromResult<IReadOnlySet<string>?>(groups);
    }

    private static string[] SplitGroups(
        string value,
        IReadOnlyList<char> separators)
        => separators.Count > 0 && value.IndexOfAny([.. separators]) >= 0
            ? value.Split([.. separators], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [value];
}
