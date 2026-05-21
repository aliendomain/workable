using Microsoft.AspNetCore.SignalR;

namespace Workable;

internal sealed class WorkableSignalRAuthenticationFilter : IHubFilter
{
    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        EnsureAuthenticated(context.Context.GetHttpContext());
        await next(context);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        EnsureAuthenticated(invocationContext.Context.GetHttpContext());
        return await next(invocationContext);
    }

    private static void EnsureAuthenticated(Microsoft.AspNetCore.Http.HttpContext? httpContext)
    {
        if (!WorkableAspNetCoreAuthentication.IsAuthenticated(httpContext))
        {
            throw new HubException("Authentication is required.");
        }
    }
}
