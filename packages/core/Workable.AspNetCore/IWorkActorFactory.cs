using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Workable;

/// <summary>
/// Creates <see cref="WorkActor"/> values from ASP.NET Core user context.
/// </summary>
public interface IWorkActorFactory
{
    /// <summary>
    /// Creates a <see cref="WorkActor"/> from an HTTP context.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns>The resolved actor.</returns>
    WorkActor Create(HttpContext? httpContext);

    /// <summary>
    /// Creates a <see cref="WorkActor"/> from a claims principal.
    /// </summary>
    /// <param name="user">The claims principal to inspect.</param>
    /// <returns>The resolved actor.</returns>
    WorkActor Create(ClaimsPrincipal? user);
}
