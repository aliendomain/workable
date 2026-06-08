using Microsoft.AspNetCore.Http;

namespace Workable;

/// <summary>
/// Creates <see cref="WorkRequestContext"/> values from ASP.NET Core request data.
/// </summary>
public sealed class HttpContextWorkRequestContextFactory(
    IWorkActorFactory actors) : IWorkRequestContextFactory
{
    /// <summary>
    /// Creates a request context from an HTTP context and invocation channel.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <param name="channel">The invocation channel to record.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <returns>The created request context.</returns>
    public WorkRequestContext Create(
        HttpContext? httpContext,
        WorkInvocationChannel channel,
        string? description = null)
    {
        var actor = actors.Create(httpContext);
        var isAuthenticated = WorkableAspNetCoreAuthentication.IsAuthenticated(httpContext);
        var url = httpContext is null
            ? null
            : $"{httpContext.Request.PathBase}{httpContext.Request.Path}{httpContext.Request.QueryString}";
        return WorkRequestContext.Create(
            channel,
            actor,
            description,
            url,
            isAuthenticated);
    }
}
