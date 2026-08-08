using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Workable;

internal static class WorkableHttpRequestContext
{
    internal static ValueTask<WorkRequestContext> Create(
        HttpContext httpContext,
        IWorkRequestContextFactory requestContexts,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.RequestServices
            .GetRequiredService<WorkableHttpRequestAccessContext>()
            .Create(systemName: null, description, cancellationToken);
    }

    internal static ValueTask<WorkRequestContext> Create(
        HttpContext httpContext,
        IWorkSystem system,
        IWorkRequestContextFactory requestContexts,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return httpContext.RequestServices
            .GetRequiredService<WorkableHttpRequestAccessContext>()
            .Create(system.Name, description, cancellationToken);
    }

    internal static async ValueTask<IWorkSystemSession> CreateSession(
        HttpContext httpContext,
        IWorkSystem system,
        IWorkRequestContextFactory requestContexts,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        return await system.CreateSession(
            await Create(httpContext, system, requestContexts, description, cancellationToken),
            cancellationToken);
    }
}
