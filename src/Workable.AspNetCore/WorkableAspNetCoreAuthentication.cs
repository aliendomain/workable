using Microsoft.AspNetCore.Http;

namespace Workable;

public static class WorkableAspNetCoreAuthentication
{
    public static bool IsAuthenticated(HttpContext? httpContext)
        => httpContext?.User?.Identity?.IsAuthenticated == true;
}
