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
    /// <remarks>
    /// Request origins retain only the path. Query strings are excluded for every channel because they can contain
    /// bearer credentials, access codes, or other caller-controlled secrets.
    /// </remarks>
    public WorkRequestContext Create(
        HttpContext? httpContext,
        WorkInvocationChannel channel,
        string? description = null)
    {
        var activeSnapshot = WorkableAspNetCoreAuthentication.GetActiveSnapshot();
        var contextSnapshot = WorkableAspNetCoreAuthentication.GetCurrentSnapshot(httpContext);
        var connectionSnapshot = ReferenceEquals(activeSnapshot, contextSnapshot)
            ? activeSnapshot
            : null;
        var actor = connectionSnapshot?.Actor ?? actors.Create(httpContext);
        var isAuthenticated = connectionSnapshot is not null || contextSnapshot is not null;
        var url = httpContext is null
            ? null
            : $"{httpContext.Request.PathBase}{httpContext.Request.Path}";
        return WorkRequestContext.Create(
            channel,
            actor,
            description,
            url,
            isAuthenticated);
    }
}
