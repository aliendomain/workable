using Microsoft.AspNetCore.Http;

namespace Workable;

public sealed class HttpContextWorkRequestContextFactory(
    IWorkActorFactory actors) : IWorkRequestContextFactory
{
    public WorkRequestContext Create(
        HttpContext? httpContext,
        WorkInvocationChannel channel,
        string? description = null)
    {
        var actor = actors.Create(httpContext);
        var url = httpContext is null
            ? null
            : $"{httpContext.Request.PathBase}{httpContext.Request.Path}{httpContext.Request.QueryString}";
        return WorkRequestContext.Create(
            channel,
            actor,
            description,
            url);
    }
}
