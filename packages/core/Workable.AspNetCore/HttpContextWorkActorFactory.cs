using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Creates <see cref="WorkActor"/> values from ASP.NET Core user context.
/// </summary>
public sealed class HttpContextWorkActorFactory : IWorkActorFactory
{
    private readonly IOptions<WorkableAspNetCoreAuthorizationOptions> options;
    private readonly IReadOnlyList<IWorkActorClaimsMapper> claimMappers;
    private readonly IWorkClaimsIdentitySelector identitySelector;

    /// <summary>
    /// Initializes an actor factory with the default primary-identity selection behavior.
    /// </summary>
    public HttpContextWorkActorFactory(IOptions<WorkableAspNetCoreAuthorizationOptions> options)
        : this(options, [], new PrimaryWorkClaimsIdentitySelector())
    {
    }

    /// <summary>
    /// Initializes an actor factory with a host-selected identity strategy.
    /// </summary>
    public HttpContextWorkActorFactory(
        IOptions<WorkableAspNetCoreAuthorizationOptions> options,
        IWorkClaimsIdentitySelector identitySelector)
        : this(options, [], identitySelector)
    {
    }

    /// <summary>
    /// Initializes an actor factory with ordered integration claim mappers and host identity selection.
    /// </summary>
    public HttpContextWorkActorFactory(
        IOptions<WorkableAspNetCoreAuthorizationOptions> options,
        IEnumerable<IWorkActorClaimsMapper> claimMappers,
        IWorkClaimsIdentitySelector identitySelector)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(claimMappers);
        this.claimMappers = claimMappers
            .OrderBy(mapper => mapper.Order)
            .ToArray();
        this.identitySelector = identitySelector ?? throw new ArgumentNullException(nameof(identitySelector));
    }

    /// <summary>
    /// Creates a <see cref="WorkActor"/> from an HTTP context.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns>The resolved actor, or <see cref="WorkActor.Unknown"/> when the user is not authenticated.</returns>
    public WorkActor Create(HttpContext? httpContext)
    {
        var snapshot = WorkableAspNetCoreAuthentication.GetCurrentSnapshot(httpContext);
        if (snapshot is null)
        {
            return WorkActor.Unknown;
        }

        snapshot.Actor ??= this.Create(snapshot.Identity);
        return snapshot.Actor;
    }

    /// <summary>
    /// Creates a <see cref="WorkActor"/> from a claims principal.
    /// </summary>
    /// <param name="user">The claims principal to inspect.</param>
    /// <returns>The resolved actor, or <see cref="WorkActor.Unknown"/> when the user is not authenticated.</returns>
    public WorkActor Create(ClaimsPrincipal? user)
    {
        if (user is null ||
            identitySelector.SelectIdentity(user) is not { IsAuthenticated: true } identity)
        {
            return WorkActor.Unknown;
        }

        return this.Create(identity);
    }

    private WorkActor Create(ClaimsIdentity? identity)
    {
        if (identity is not { IsAuthenticated: true })
        {
            return WorkActor.Unknown;
        }

        foreach (var mapper in this.claimMappers)
        {
            if (mapper.TryCreate(identity, out var mappedActor))
            {
                return mappedActor;
            }
        }

        return new WorkActor(
            Id: FindFirst(identity, options.Value.ActorIdClaimTypes),
            Name: identity.Name ?? FindFirst(identity, options.Value.ActorNameClaimTypes),
            Email: FindFirst(identity, options.Value.ActorEmailClaimTypes));
    }

    private static string? FindFirst(
        ClaimsIdentity identity,
        IEnumerable<string> claimTypes)
        => claimTypes
            .Select(identity.FindFirst)
            .FirstOrDefault(claim => !string.IsNullOrWhiteSpace(claim?.Value))
            ?.Value;
}
