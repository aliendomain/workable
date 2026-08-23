using System.Collections.Frozen;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Resolves authorization groups from the claims principal selected for Workable.
/// </summary>
public sealed class HttpContextClaimsWorkAuthorizationGroupProvider : IWorkAuthorizationGroupContextProvider
{
    private static readonly object GroupsCacheKey = new();
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IWorkActorFactory? actors;
    private readonly IOptions<WorkableAspNetCoreAuthorizationOptions> options;
    private readonly IReadOnlyList<IWorkAuthorizationGroupClaimMapper>? claimMappers;
    private readonly IWorkClaimsIdentitySelector? identitySelector;
    private readonly IServiceProvider? fallbackServices;

    /// <inheritdoc />
    public int Order => 1000;

    /// <summary>
    /// Initializes a provider that uses the generic claim mappings in
    /// <see cref="WorkableAspNetCoreAuthorizationOptions"/>.
    /// </summary>
    public HttpContextClaimsWorkAuthorizationGroupProvider(
        IHttpContextAccessor httpContextAccessor,
        IWorkActorFactory actors,
        IOptions<WorkableAspNetCoreAuthorizationOptions> options)
        : this(httpContextAccessor, actors, options, [], new PrimaryWorkClaimsIdentitySelector())
    {
    }

    /// <summary>
    /// Initializes a provider with ordered claim mappers and the generic claim mapping fallback.
    /// </summary>
    public HttpContextClaimsWorkAuthorizationGroupProvider(
        IHttpContextAccessor httpContextAccessor,
        IWorkActorFactory actors,
        IOptions<WorkableAspNetCoreAuthorizationOptions> options,
        IEnumerable<IWorkAuthorizationGroupClaimMapper> claimMappers)
        : this(httpContextAccessor, actors, options, claimMappers, new PrimaryWorkClaimsIdentitySelector())
    {
    }

    /// <summary>
    /// Initializes a provider with ordered claim mappers, generic mapping fallback, and host identity selection.
    /// </summary>
    public HttpContextClaimsWorkAuthorizationGroupProvider(
        IHttpContextAccessor httpContextAccessor,
        IWorkActorFactory actors,
        IOptions<WorkableAspNetCoreAuthorizationOptions> options,
        IEnumerable<IWorkAuthorizationGroupClaimMapper> claimMappers,
        IWorkClaimsIdentitySelector identitySelector)
    {
        this.httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        this.actors = actors ?? throw new ArgumentNullException(nameof(actors));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(claimMappers);
        this.claimMappers = claimMappers
            .OrderBy(mapper => mapper.Order)
            .ToArray();
        this.identitySelector = identitySelector ?? throw new ArgumentNullException(nameof(identitySelector));
    }

    internal HttpContextClaimsWorkAuthorizationGroupProvider(
        IHttpContextAccessor httpContextAccessor,
        IOptions<WorkableAspNetCoreAuthorizationOptions> options,
        IServiceProvider fallbackServices)
    {
        this.httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.fallbackServices = fallbackServices ?? throw new ArgumentNullException(nameof(fallbackServices));
    }

    /// <summary>
    /// Gets the group values associated with the current authenticated Workable principal.
    /// </summary>
    /// <param name="actor">The actor being authorized.</param>
    /// <param name="systemName">The system name being authorized, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="cancellationToken">A token that cancels group resolution.</param>
    /// <returns>The resolved group values for the current authenticated user.</returns>
    public ValueTask<IReadOnlySet<string>?> GetCurrentGroups(
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken = default)
        => this.GetCurrentGroupsCore(
            this.httpContextAccessor.HttpContext,
            actor,
            systemName,
            cancellationToken,
            preferActiveSnapshot: true);

    internal ValueTask<IReadOnlySet<string>?> GetCurrentGroups(
        HttpContext? httpContext,
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken = default)
        => this.GetCurrentGroupsCore(
            httpContext,
            actor,
            systemName,
            cancellationToken,
            preferActiveSnapshot: false);

    private ValueTask<IReadOnlySet<string>?> GetCurrentGroupsCore(
        HttpContext? httpContext,
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken,
        bool preferActiveSnapshot)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeSnapshot = WorkableAspNetCoreAuthentication.GetActiveSnapshot();
        var snapshot = preferActiveSnapshot
            ? activeSnapshot ?? WorkableAspNetCoreAuthentication.GetCurrentSnapshot(httpContext)
            : WorkableAspNetCoreAuthentication.GetCurrentSnapshot(httpContext);
        var user = snapshot?.Principal;
        if (snapshot is null || user is null)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(null);
        }

        var snapshotActor = snapshot.Actor;
        if (snapshotActor is not null && snapshotActor != actor)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(null);
        }

        if (snapshot.ClaimsGroups is not null)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(snapshot.ClaimsGroups);
        }

        if ((preferActiveSnapshot && activeSnapshot is not null) || httpContext is null)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(null);
        }

        if (httpContext.Items.TryGetValue(GroupsCacheKey, out var cached) &&
            cached is IReadOnlySet<string> cachedGroups)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(cachedGroups);
        }

        var requestServices = httpContext.RequestServices ?? this.fallbackServices!;
        var currentActor = snapshotActor;
        if (currentActor is null)
        {
            var actors = this.actors ?? requestServices.GetRequiredService<IWorkActorFactory>();
            currentActor = this.actors is null
                ? actors.Create(httpContext)
                : actors.Create(user);
        }

        if (currentActor != actor)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(null);
        }

        var identity = this.identitySelector is null
            ? WorkableAspNetCoreAuthentication.GetCurrentIdentity(httpContext)
            : this.identitySelector.SelectIdentity(user);
        if (identity is null)
        {
            return ValueTask.FromResult<IReadOnlySet<string>?>(null);
        }

        var claimMappers = this.claimMappers ?? requestServices
            .GetServices<IWorkAuthorizationGroupClaimMapper>()
            .OrderBy(mapper => mapper.Order)
            .ToArray();
        var groups = identity.Claims
            .SelectMany(claim => MapGroups(identity, claim, claimMappers))
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        httpContext.Items[GroupsCacheKey] = groups;
        if (this.identitySelector is null)
        {
            snapshot.ClaimsGroups = groups;
        }

        return ValueTask.FromResult<IReadOnlySet<string>?>(groups);
    }

    private IEnumerable<string> MapGroups(
        ClaimsIdentity identity,
        Claim claim,
        IReadOnlyList<IWorkAuthorizationGroupClaimMapper> claimMappers)
    {
        foreach (var mapper in claimMappers)
        {
            if (mapper.TryMap(identity, claim, out var mappedGroups))
            {
                return mappedGroups;
            }
        }

        return options.Value.GroupClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase)
            ? SplitGroups(claim.Value, GetSeparators(options.Value, claim.Type))
            : [];
    }

    private static string[] SplitGroups(
        string value,
        IReadOnlyList<char> separators)
        => separators.Count > 0 && value.IndexOfAny([.. separators]) >= 0
            ? value.Split([.. separators], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [value];

    private static IReadOnlyList<char> GetSeparators(
        WorkableAspNetCoreAuthorizationOptions options,
        string claimType)
        => options.GroupClaimValueSeparatorsByClaimType.TryGetValue(claimType, out var separators)
            ? separators
            : options.GroupClaimValueSeparators;
}

internal sealed class RequestScopedHttpContextClaimsWorkAuthorizationGroupProvider(
    IHttpContextAccessor httpContextAccessor,
    IOptions<WorkableAspNetCoreAuthorizationOptions> options,
    IServiceProvider services)
    : IWorkAuthorizationGroupContextProvider
{
    private readonly HttpContextClaimsWorkAuthorizationGroupProvider inner = new(
        httpContextAccessor,
        options,
        services);

    public int Order => this.inner.Order;

    public ValueTask<IReadOnlySet<string>?> GetCurrentGroups(
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken = default)
        => this.inner.GetCurrentGroups(actor, systemName, cancellationToken);

    internal ValueTask<IReadOnlySet<string>?> GetCurrentGroups(
        HttpContext httpContext,
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken = default)
        => this.inner.GetCurrentGroups(httpContext, actor, systemName, cancellationToken);
}
