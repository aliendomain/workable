using Microsoft.AspNetCore.Http;

namespace Workable;

public interface IWorkRequestContextFactory
{
    WorkRequestContext Create(
        HttpContext? httpContext,
        WorkInvocationChannel channel,
        string description);
}
