using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal static class WorkableHttpRequestContext
{
    internal static WorkRequestContext Create(
        HttpContext httpContext,
        IWorkRequestContextFactory requestContexts,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.RequestServices
            .GetRequiredService<WorkableHttpRequestAccessContext>()
            .Create(systemName: null, description);
    }

    internal static WorkRequestContext Create(
        HttpContext httpContext,
        IWorkSystem system,
        IWorkRequestContextFactory requestContexts,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(system);

        return httpContext.RequestServices
            .GetRequiredService<WorkableHttpRequestAccessContext>()
            .Create(system.Name, description);
    }

    internal static IWorkSystemSession CreateSession(
        HttpContext httpContext,
        IWorkSystem system,
        IWorkRequestContextFactory requestContexts,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system.CreateSession(Create(httpContext, system, requestContexts, description));
    }
}
