using Microsoft.AspNetCore.SignalR;

namespace Workable;

internal sealed class WorkableSignalRAuthorizationFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (WorkSystemAccessDeniedException denied)
        {
            throw new HubException(denied.Message);
        }
    }
}
