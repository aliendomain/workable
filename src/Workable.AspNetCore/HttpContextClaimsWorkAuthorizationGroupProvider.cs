using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Workable;

public sealed class HttpContextClaimsWorkAuthorizationGroupProvider(
    IHttpContextAccessor httpContextAccessor,
    IOptions<WorkableAspNetCoreAuthorizationOptions> options) : IWorkAuthorizationGroupProvider
{
    private static readonly object GroupsCacheKey = new();
    private const string DefaultSystemCacheKey = "<default>";

    public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var cacheKey = string.IsNullOrWhiteSpace(systemName) ? DefaultSystemCacheKey : systemName;
        if (httpContext is null || httpContext.User.Identity?.IsAuthenticated != true)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var user = httpContext.User;
        Dictionary<string, IReadOnlySet<string>>? cache = null;
        if (httpContext.Items[GroupsCacheKey] is Dictionary<string, IReadOnlySet<string>> existingCache)
        {
            cache = existingCache;
        }

        if (cache is not null && cache.TryGetValue(cacheKey, out var cachedGroups))
        {
            return cachedGroups;
        }

        var groups = user.Claims
            .Where(claim => options.Value.GroupClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .SelectMany(claim => SplitGroups(claim.Value, options.Value.GroupClaimValueSeparators))
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        cache ??= new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        cache[cacheKey] = groups;
        httpContext.Items[GroupsCacheKey] = cache;

        return groups;
    }

    private static string[] SplitGroups(
        string value,
        IReadOnlyList<char> separators)
        => separators.Count > 0 && value.IndexOfAny([.. separators]) >= 0
            ? value.Split([.. separators], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [value];
}
