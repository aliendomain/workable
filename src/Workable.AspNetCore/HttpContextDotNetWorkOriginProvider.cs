using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Workable;

public sealed class HttpContextDotNetWorkOriginProvider(IHttpContextAccessor httpContextAccessor) : IDotNetWorkOriginProvider
{
    public WorkOrigin CreateOrigin(string description)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return WorkOrigin.Create(WorkInvocationChannel.DotNet, description: description);
        }

        return WorkOrigin.Create(
            WorkInvocationChannel.DotNet,
            CreateActor(httpContext.User),
            description,
            $"{httpContext.Request.PathBase}{httpContext.Request.Path}{httpContext.Request.QueryString}");
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
