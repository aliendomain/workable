using Microsoft.AspNetCore.Http;

namespace Workable;

internal static class WorkableHttpRouteResults
{
    internal static async Task<IResult> ToOk<T>(Task<T> task)
        => Results.Ok(await task);

    internal static bool TryResolveSystem(
        HttpContext httpContext,
        WorkableHttpSystemResolver systems,
        out IWorkSystem system,
        out IResult notFound)
    {
        var systemName = httpContext.Request.RouteValues.TryGetValue("systemName", out var value)
            ? Convert.ToString(value)
            : null;
        if (systems.TryGetSystem(systemName, out var resolved))
        {
            system = resolved;
            notFound = Results.NotFound();
            return true;
        }

        system = null!;
        notFound = Results.NotFound(new
        {
            Messages = new[]
            {
                WorkMessage.Error("workable.http.system.not_found", $"Workable system '{systemName}' was not found.", "systemName"),
            },
        });
        return false;
    }

    internal static IResult ToQueueHttpResult(WorkableHttpWorkResult result)
        => result.Status == WorkableHttpWorkStatus.Rejected
            ? Results.BadRequest(result)
            : Results.Ok(result);

    internal static IResult ToActionHttpResult(WorkActionOutcome result)
        => result.Status switch
        {
            WorkActionStatus.Accepted => Results.Ok(result),
            WorkActionStatus.NotFound => Results.NotFound(result),
            WorkActionStatus.Conflict => Results.Conflict(result),
            _ => Results.BadRequest(result),
        };

    internal static IResult ToDefinitionReconfigurationHttpResult(WorkDefinitionReconfigurationOutcome result)
        => result.Status switch
        {
            WorkDefinitionReconfigurationStatus.Accepted => Results.Ok(result),
            WorkDefinitionReconfigurationStatus.NotFound => Results.NotFound(result),
            WorkDefinitionReconfigurationStatus.Conflict => Results.Conflict(result),
            _ => Results.BadRequest(result),
        };
}
