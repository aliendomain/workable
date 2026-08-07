using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

internal static class WorkableHttpProfilingRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/profiling/capture-rules", async (
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess) =>
        {
            if (!TryResolve(httpContext, topology, out var system, out var rules, out var notFound))
            {
                return notFound;
            }

            await EnsureDiagnosticsAccess(system, requestAccess, httpContext.RequestAborted);
            return Results.Ok(CreateState(rules));
        });

        group.MapPost("/profiling/capture-rules", async (
            WorkableHttpCreateProfilingCaptureRuleRequest request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess,
            IWorkRequestContextFactory requestContexts) =>
        {
            if (!TryResolve(httpContext, topology, out var system, out var rules, out var notFound))
            {
                return notFound;
            }

            await EnsureDiagnosticsAccess(system, requestAccess, httpContext.RequestAborted);
            try
            {
                var requestContext = await WorkableHttpRequestContext.Create(
                    httpContext,
                    system,
                    requestContexts,
                    request.Description);
                var created = rules.CreateProfileCaptureRule(
                    request.DefinitionName,
                    request.ActorId,
                    request.MaximumMatches,
                    TimeSpan.FromMinutes(request.ExpiresAfterMinutes),
                    requestContext.Actor);
                return Results.Ok(ToHttp(created));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.profiling.capture_rule.invalid",
                            exception.Message,
                            "captureRule"),
                    },
                });
            }
        });

        group.MapDelete("/profiling/capture-rules/{ruleId:guid}", async (
            Guid ruleId,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess) =>
        {
            if (!TryResolve(httpContext, topology, out var system, out var rules, out var notFound))
            {
                return notFound;
            }

            await EnsureDiagnosticsAccess(system, requestAccess, httpContext.RequestAborted);
            return rules.DeleteProfileCaptureRule(ruleId)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    private static bool TryResolve(
        HttpContext httpContext,
        WorkableHttpTopologyResolver topology,
        out IWorkSystem system,
        out IWorkProfileCaptureRuleSystem rules,
        out IResult notFound)
    {
        if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out system, out notFound))
        {
            rules = null!;
            return false;
        }

        rules = system as IWorkProfileCaptureRuleSystem
            ?? throw new InvalidOperationException("The Workable system does not expose profile capture rules.");
        return true;
    }

    private static async ValueTask EnsureDiagnosticsAccess(
        IWorkSystem system,
        WorkableHttpRequestAccessContext requestAccess,
        CancellationToken cancellationToken)
    {
        if (!(await requestAccess.DescribeAccess(system, cancellationToken)).CanViewDiagnostics)
        {
            throw new WorkSystemAccessDeniedException(
                WorkSystemPermission.ViewDiagnostics,
                system.Id,
                system.Name);
        }
    }

    private static WorkableHttpProfilingCaptureState CreateState(IWorkProfileCaptureRuleSystem rules)
        => new(
            rules.ProfilingConfiguration.MaximumAutomaticInstrumentationNodes,
            [.. rules.GetProfileCaptureRules().Select(ToHttp)]);

    private static WorkableHttpProfilingCaptureRule ToHttp(WorkProfileCaptureRuleSnapshot rule)
        => new(
            rule.Id,
            rule.DefinitionName,
            rule.ActorId,
            rule.MaximumMatches,
            rule.RemainingMatches,
            rule.CreatedAt,
            rule.ExpiresAt,
            rule.CreatedBy);
}
