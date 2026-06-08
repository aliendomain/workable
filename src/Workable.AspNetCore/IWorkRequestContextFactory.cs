using Microsoft.AspNetCore.Http;

namespace Workable;

/// <summary>
/// Creates <see cref="WorkRequestContext"/> values from ASP.NET Core request data.
/// </summary>
public interface IWorkRequestContextFactory
{
    /// <summary>
    /// Creates a request context from an HTTP context and invocation channel.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <param name="channel">The invocation channel to record.</param>
    /// <param name="description">Optional caller-supplied request description.</param>
    /// <returns>The created request context.</returns>
    WorkRequestContext Create(
        HttpContext? httpContext,
        WorkInvocationChannel channel,
        string? description = null);
}
