using Microsoft.AspNetCore.Http;

namespace Workable;

internal sealed class WorkableHttpRequestAccessContext(
    IHttpContextAccessor httpContextAccessor,
    IWorkRequestContextFactory requestContexts,
    IWorkAuthorizationGroupResolver groupResolver)
{
    // This scoped cache is per HTTP request. Regular Dictionary is intentional here because the built-in
    // adapter uses it from the normal request pipeline, not from parallel authorization work inside one request.
    // If built-in route authorization ever starts doing parallel per-request evaluation, add synchronization.
    private readonly Dictionary<SystemCacheKey, IReadOnlySet<string>> groupsBySystem = new(SystemCacheKeyComparer.Instance);
    private readonly Dictionary<SystemCacheKey, WorkAuthorizationSnapshot> authorizationBySystem = new(SystemCacheKeyComparer.Instance);
    private readonly Dictionary<SystemCacheKey, WorkSystemAccessSummary> accessBySystem = new(SystemCacheKeyComparer.Instance);
    private readonly Dictionary<SystemCacheKey, bool> builtInSurfaceAccessBySystem = new(SystemCacheKeyComparer.Instance);
    private WorkRequestContext? baseContext;

    internal async ValueTask<WorkRequestContext> Create(
        string? systemName = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = this.GetBaseContext();
        return requestContext with
        {
            Description = description,
            Authorization = await this.GetAuthorization(systemName, requestContext.Actor, cancellationToken),
        };
    }

    internal async ValueTask<bool> HasAnyRequiredSurfaceGroup(
        IReadOnlySet<string> requiredGroups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredGroups);

        return (await this.GetGroups(systemName: null, cancellationToken)).Any(requiredGroups.Contains);
    }

    internal async ValueTask<bool> IsBuiltInSurfaceAllowed(
        IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        var cacheKey = GetCacheKey(system.Name);
        if (this.builtInSurfaceAccessBySystem.TryGetValue(cacheKey, out var allowed))
        {
            return allowed;
        }

        if (this.accessBySystem.TryGetValue(cacheKey, out var access) &&
            WorkableHttpBuiltInSurfaceAccess.IsAllowed(access))
        {
            this.builtInSurfaceAccessBySystem[cacheKey] = true;
            return true;
        }

        var requestContext = await this.Create(system.Name, cancellationToken: cancellationToken);
        if (system is IWorkSystemBuiltInHttpSurfaceAccess fastPath)
        {
            allowed = await fastPath.IsBuiltInHttpSurfaceAllowed(requestContext, cancellationToken);
            this.builtInSurfaceAccessBySystem[cacheKey] = allowed;
            return allowed;
        }

        access = await system.DescribeAccess(requestContext, cancellationToken);
        this.accessBySystem[cacheKey] = access;
        allowed = WorkableHttpBuiltInSurfaceAccess.IsAllowed(access);
        this.builtInSurfaceAccessBySystem[cacheKey] = allowed;
        return allowed;
    }

    internal async ValueTask<bool> HasAnySystemAccess(
        IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return (await this.DescribeAccess(system, cancellationToken)).HasAnyAccess();
    }

    internal async ValueTask<WorkSystemAccessSummary> DescribeAccess(
        IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        var cacheKey = GetCacheKey(system.Name);
        if (this.accessBySystem.TryGetValue(cacheKey, out var access))
        {
            return access;
        }

        access = await system.DescribeAccess(
            await this.Create(system.Name, cancellationToken: cancellationToken),
            cancellationToken);
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

    private async ValueTask<WorkAuthorizationSnapshot> GetAuthorization(
        string? systemName,
        WorkActor actor,
        CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(systemName);
        if (this.authorizationBySystem.TryGetValue(cacheKey, out var authorization))
        {
            return authorization;
        }

        authorization = WorkAuthorizationSnapshot.CreateForSystem(
            systemName,
            actor,
            await this.GetGroups(systemName, cancellationToken),
            readableDefinitionIds: null,
            isAuthenticated: this.GetBaseContext().IsAuthenticated);
        this.authorizationBySystem[cacheKey] = authorization;
        return authorization;
    }

    private async ValueTask<IReadOnlySet<string>> GetGroups(
        string? systemName,
        CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(systemName);
        if (this.groupsBySystem.TryGetValue(cacheKey, out var groups))
        {
            return groups;
        }

        groups = await groupResolver.GetGroups(
            this.GetBaseContext(),
            systemName,
            cancellationToken);
        this.groupsBySystem[cacheKey] = groups;
        return groups;
    }

    private static SystemCacheKey GetCacheKey(string? systemName)
        => new(systemName);

    private readonly record struct SystemCacheKey(string? Name);

    private sealed class SystemCacheKeyComparer : IEqualityComparer<SystemCacheKey>
    {
        public static SystemCacheKeyComparer Instance { get; } = new();

        public bool Equals(SystemCacheKey left, SystemCacheKey right)
            => left.Name is null
                ? right.Name is null
                : right.Name is not null && StringComparer.OrdinalIgnoreCase.Equals(left.Name, right.Name);

        public int GetHashCode(SystemCacheKey key)
            => key.Name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name);
    }
}
