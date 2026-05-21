using Microsoft.AspNetCore.SignalR;

namespace Workable;

internal sealed class WorkableSignalRAuthenticationFilter : IHubFilter
{
    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        await EnsureAuthenticatedAsync(context.Context.GetHttpContext());
        await next(context);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        await EnsureAuthenticatedAsync(invocationContext.Context.GetHttpContext());
        return await next(invocationContext);
    }

    private static async Task EnsureAuthenticatedAsync(Microsoft.AspNetCore.Http.HttpContext? httpContext)
    {
        if (!await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
        {
            throw new HubException("Authentication is required.");
        }
    }
}
