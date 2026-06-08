using Microsoft.AspNetCore.Http;

namespace Workable;

internal static class WorkableHttpRequestContext
{
    internal static WorkRequestContext Create(
        HttpContext httpContext,
        IWorkRequestContextFactory requestContexts,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(requestContexts);

        return requestContexts.Create(httpContext, WorkInvocationChannel.HttpApi, description);
    }

    internal static IWorkSystemSession CreateSession(
        HttpContext httpContext,
        IWorkSystem system,
        IWorkRequestContextFactory requestContexts,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system.CreateSession(Create(httpContext, requestContexts, description));
    }
}
