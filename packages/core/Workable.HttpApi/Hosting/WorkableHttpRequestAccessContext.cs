using Microsoft.AspNetCore.Http;

namespace Workable;

internal sealed class WorkableHttpRequestAccessContext(
    IHttpContextAccessor httpContextAccessor,
    IWorkRequestContextFactory requestContexts,
    IWorkAuthorizationGroupProvider groupProvider)
{
    private const string DefaultSystemCacheKey = "<default>";

    // This scoped cache is per HTTP request. Regular Dictionary is intentional here because the built-in
    // adapter uses it from the normal request pipeline, not from parallel authorization work inside one request.
    // If built-in route authorization ever starts doing parallel per-request evaluation, add synchronization.
    private readonly Dictionary<string, IReadOnlySet<string>> groupsBySystem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkAuthorizationSnapshot> authorizationBySystem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkSystemAccessSummary> accessBySystem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> builtInSurfaceAccessBySystem = new(StringComparer.OrdinalIgnoreCase);
    private WorkRequestContext? baseContext;

    internal WorkRequestContext Create(
        string? systemName = null,
        string? description = null)
    {
        var requestContext = this.GetBaseContext();
        return requestContext with
        {
            Description = description,
            Authorization = this.GetAuthorization(systemName, requestContext.Actor),
        };
    }

    internal bool HasAnyRequiredSurfaceGroup(IReadOnlySet<string> requiredGroups)
    {
        ArgumentNullException.ThrowIfNull(requiredGroups);

        return this.GetGroups(systemName: null).Any(requiredGroups.Contains);
    }

    internal bool IsBuiltInSurfaceAllowed(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        var cacheKey = GetCacheKey(system.Name);
        if (this.builtInSurfaceAccessBySystem.TryGetValue(cacheKey, out var allowed))
        {
            return allowed;
        }

        if (this.accessBySystem.TryGetValue(cacheKey, out var access))
        {
            if (WorkableHttpBuiltInSurfaceAccess.IsAllowed(access))
            {
                this.builtInSurfaceAccessBySystem[cacheKey] = true;
                return true;
            }
        }

        var requestContext = this.Create(system.Name);
        if (system is IWorkSystemBuiltInHttpSurfaceAccess fastPath)
        {
            allowed = fastPath.IsBuiltInHttpSurfaceAllowed(requestContext);
            this.builtInSurfaceAccessBySystem[cacheKey] = allowed;
            return allowed;
        }

        access = system.DescribeAccess(requestContext);
        this.accessBySystem[cacheKey] = access;
        allowed = WorkableHttpBuiltInSurfaceAccess.IsAllowed(access);
        this.builtInSurfaceAccessBySystem[cacheKey] = allowed;
        return allowed;
    }

    internal bool HasAnySystemAccess(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return this.DescribeAccess(system).HasAnyAccess();
    }

    internal WorkSystemAccessSummary DescribeAccess(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        var cacheKey = GetCacheKey(system.Name);
        if (this.accessBySystem.TryGetValue(cacheKey, out var access))
        {
            return access;
        }

        access = system.DescribeAccess(this.Create(system.Name));
        this.accessBySystem[cacheKey] = access;
        if (WorkableHttpBuiltInSurfaceAccess.IsAllowed(access))
        {
            this.builtInSurfaceAccessBySystem[cacheKey] = true;
        }

        return access;
    }

    private WorkRequestContext GetBaseContext()
    {
        if (this.baseContext is not null)
        {
            return this.baseContext;
        }

        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("The built-in Workable HTTP API request context is only available during an active HTTP request.");
        this.baseContext = requestContexts.Create(httpContext, WorkInvocationChannel.HttpApi)
            .WithSurface(WorkOriginSurface.WorkableAdapter);
        return this.baseContext;
    }

    private WorkAuthorizationSnapshot GetAuthorization(
        string? systemName,
        WorkActor actor)
    {
        var cacheKey = GetCacheKey(systemName);
        if (this.authorizationBySystem.TryGetValue(cacheKey, out var authorization))
        {
            return authorization;
        }

        authorization = WorkAuthorizationSnapshot.Create(
            actor,
            this.GetGroups(systemName),
            readableDefinitionIds: null);
        this.authorizationBySystem[cacheKey] = authorization;
        return authorization;
    }

    private IReadOnlySet<string> GetGroups(string? systemName)
    {
        var cacheKey = GetCacheKey(systemName);
        if (this.groupsBySystem.TryGetValue(cacheKey, out var groups))
        {
            return groups;
        }

        groups = NormalizeGroups(groupProvider.GetGroups(this.GetBaseContext().Actor, systemName));
        this.groupsBySystem[cacheKey] = groups;
        return groups;
    }

    private static IReadOnlySet<string> NormalizeGroups(IEnumerable<string>? groups)
        => groups?
            .Where(static group => !string.IsNullOrWhiteSpace(group))
            .Select(static group => group.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string GetCacheKey(string? systemName)
        => string.IsNullOrWhiteSpace(systemName) ? DefaultSystemCacheKey : systemName;
}
