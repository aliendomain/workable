using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Workable;

public interface IWorkActorFactory
{
    WorkActor Create(HttpContext? httpContext);

    WorkActor Create(ClaimsPrincipal? user);
}
