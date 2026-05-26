using Microsoft.AspNetCore.Http;

namespace Workable;

internal static class WorkableHttpRouteResults
{
    internal static IResult AuthenticationRequired()
        => Results.Json(new
        {
            Messages = new[]
            {
                WorkMessage.Error("workable.http.authentication_required", "Authentication is required.", "user"),
            },
        }, statusCode: StatusCodes.Status401Unauthorized);

    internal static IResult AuthorizationDenied(WorkSystemAccessDeniedException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var code = exception.Permission switch
        {
            WorkSystemPermission.Connect => "workable.http.system.connect_denied",
            WorkSystemPermission.ViewDiagnostics => "workable.http.system.diagnostics_denied",
            WorkSystemPermission.ControlSystem => "workable.http.system.control_denied",
            _ => "workable.http.system.authorization_denied",
        };

        return Results.Json(new
        {
            Messages = new[]
            {
                WorkMessage.Error(code, exception.Message, "system"),
            },
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    internal static async Task<IResult> ToOk<T>(Func<Task<T>> action)
        => Results.Ok(await action());

    internal static bool TryResolveSystem(
        HttpContext httpContext,
        WorkableHttpTopologyResolver topology,
        out IWorkSystem system,
        out IResult notFound)
    {
        var systemName = httpContext.Request.RouteValues.TryGetValue("systemName", out var value)
            ? Convert.ToString(value)
            : null;
        if (topology.TryResolveSystem(systemName, out var resolved))
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
        => result.Status != WorkableHttpWorkStatus.Rejected
            ? Results.Ok(result)
            : result.QueueOutcome.Status switch
            {
                WorkQueueStatus.NotFound => Results.NotFound(result),
                WorkQueueStatus.Unauthorized => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.BadRequest(result),
            };

    internal static IResult ToActionHttpResult(WorkActionOutcome result)
        => result.Status switch
        {
            WorkActionStatus.Accepted => Results.Ok(result),
            WorkActionStatus.NotFound => Results.NotFound(result),
            WorkActionStatus.Unauthorized => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
            WorkActionStatus.Conflict => Results.Conflict(result),
            _ => Results.BadRequest(result),
        };

    internal static IResult ToDefinitionReconfigurationHttpResult(WorkDefinitionReconfigurationOutcome result)
        => result.Status switch
        {
            WorkDefinitionReconfigurationStatus.Accepted => Results.Ok(result),
            WorkDefinitionReconfigurationStatus.NotFound => Results.NotFound(result),
            WorkDefinitionReconfigurationStatus.Unauthorized => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
            WorkDefinitionReconfigurationStatus.Conflict => Results.Conflict(result),
            _ => Results.BadRequest(result),
        };
}
