using Microsoft.AspNetCore.Http;

namespace Workable;

public sealed class HttpContextDotNetWorkOriginProvider(
    IWorkRequestContextFactory requestContexts,
    IHttpContextAccessor httpContextAccessor) : IDotNetWorkOriginProvider
{
    public WorkOrigin CreateOrigin(string description)
    {
        try
        {
            var httpContext = httpContextAccessor.HttpContext;
            return httpContext is null
                ? CreateDotNetOrigin(description)
                : requestContexts.Create(httpContext, WorkInvocationChannel.DotNet, description).Origin;
        }
        catch (ObjectDisposedException)
        {
            return CreateDotNetOrigin(description);
        }
    }

    private static WorkOrigin CreateDotNetOrigin(string description)
        => WorkOrigin.Create(WorkInvocationChannel.DotNet, description: description);
}
