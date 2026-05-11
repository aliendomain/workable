using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Workable;

internal static class WorkableMcpOrigin
{
    public static WorkOrigin Create(HttpContext? httpContext, string description)
    {
        return WorkOrigin.Create(
            WorkInvocationChannel.Mcp,
            httpContext is null ? WorkActor.Unknown : CreateActor(httpContext.User),
            description,
            httpContext is null
                ? null
                : $"{httpContext.Request.PathBase}{httpContext.Request.Path}{httpContext.Request.QueryString}");
    }

    private static WorkActor CreateActor(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return WorkActor.Unknown;
        }

        return new WorkActor(
            Id: user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value,
            Name: user.Identity?.Name ?? user.FindFirst(ClaimTypes.Name)?.Value,
            Email: user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("email")?.Value);
    }
}
