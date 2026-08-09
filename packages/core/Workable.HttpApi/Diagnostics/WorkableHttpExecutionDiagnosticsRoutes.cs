using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Workable;

internal static class WorkableHttpExecutionDiagnosticsRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/execution-diagnostics", async (
            string? definitionName,
            Guid? workerId,
            DateTimeOffset? completedAfter,
            DateTimeOffset? completedBefore,
            LogLevel? minimumLogLevel,
            int? take,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess,
            CancellationToken cancellationToken) =>
        {
            if (take is <= 0 or > 1_000)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.execution_diagnostics.query.invalid",
                            "Execution diagnostic query take must be between 1 and 1,000.",
                            "take"),
                    },
                });
            }

            if (!TryResolve(httpContext, topology, out var system, out var diagnostics, out var notFound))
            {
                return notFound;
            }

            await EnsureDiagnosticsAccess(system, requestAccess, cancellationToken);
            var result = await diagnostics.QueryExecutionDiagnostics(
                new WorkExecutionDiagnosticCriteria(
                    system.Id,
                    definitionName,
                    workerId is null ? null : new WorkerId(workerId.Value),
                    completedAfter,
                    completedBefore,
                    minimumLogLevel,
                    Take: take ?? 100),
                cancellationToken);
            return Results.Ok(result);
        });

        group.MapGet("/execution-diagnostics/workers/{workerId:guid}/iterations/{sequence:long}", async (
            Guid workerId,
            long sequence,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolve(httpContext, topology, out var system, out var diagnostics, out var notFound))
            {
                return notFound;
            }

            await EnsureDiagnosticsAccess(system, requestAccess, cancellationToken);
            var artifact = await diagnostics.GetExecutionDiagnostic(
                new WorkExecutionDiagnosticGetRequest(system.Id, new WorkerId(workerId), sequence),
                cancellationToken);
            return artifact is null ? Results.NotFound() : Results.Ok(artifact);
        });

        group.MapGet("/execution-diagnostics/capture-rules", async (
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolve(httpContext, topology, out var system, out var diagnostics, out var notFound))
            {
                return notFound;
            }

            await EnsureDiagnosticsAccess(system, requestAccess, cancellationToken);
            return Results.Ok(new WorkableHttpExecutionDiagnosticCaptureState(
                diagnostics.ExecutionDiagnosticsPersistenceAvailable,
                diagnostics.GetExecutionDiagnosticCaptureRules()));
        });

        group.MapPost("/execution-diagnostics/capture-rules", async (
            WorkableHttpCreateExecutionDiagnosticCaptureRuleRequest request,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolve(httpContext, topology, out var system, out var diagnostics, out var notFound))
            {
                return notFound;
            }

            await EnsureControlSystemAccess(system, requestAccess, cancellationToken);
            if (!diagnostics.ExecutionDiagnosticsPersistenceAvailable)
            {
                return Results.Conflict(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.execution_diagnostics.persistence_required",
                            "Register an execution diagnostics repository before creating a capture rule.",
                            "captureRule"),
                    },
                });
            }

            try
            {
                var requestContext = await WorkableHttpRequestContext.Create(
                    httpContext,
                    system,
                    requestContexts,
                    request.Description);
                var rule = await diagnostics.CreateExecutionDiagnosticCaptureRule(
                    request.DefinitionName,
                    request.MinimumLogLevel,
                    request.ProfileCaptureMode,
                    TimeSpan.FromMinutes(request.ActiveForMinutes),
                    TimeSpan.FromMinutes(request.ArtifactRetentionMinutes),
                    requestContext.Actor,
                    cancellationToken);
                return Results.Ok(rule);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.execution_diagnostics.capture_rule.invalid",
                            exception.Message,
                            "captureRule"),
                    },
                });
            }
        });

        group.MapDelete("/execution-diagnostics/capture-rules/{ruleId:guid}", async (
            Guid ruleId,
            HttpContext httpContext,
            WorkableHttpTopologyResolver topology,
            WorkableHttpRequestAccessContext requestAccess,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolve(httpContext, topology, out var system, out var diagnostics, out var notFound))
            {
                return notFound;
            }

            await EnsureControlSystemAccess(system, requestAccess, cancellationToken);
            return await diagnostics.DeleteExecutionDiagnosticCaptureRule(ruleId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    private static bool TryResolve(
        HttpContext httpContext,
        WorkableHttpTopologyResolver topology,
        out IWorkSystem system,
        out IWorkExecutionDiagnosticsSystem diagnostics,
        out IResult notFound)
    {
        if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out system, out notFound))
        {
            diagnostics = null!;
            return false;
        }

        diagnostics = system as IWorkExecutionDiagnosticsSystem
            ?? throw new InvalidOperationException("The Workable system does not expose execution diagnostics.");
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

    private static async ValueTask EnsureControlSystemAccess(
        IWorkSystem system,
        WorkableHttpRequestAccessContext requestAccess,
        CancellationToken cancellationToken)
    {
        if (!(await requestAccess.DescribeAccess(system, cancellationToken)).CanControlSystem)
        {
            throw new WorkSystemAccessDeniedException(
                WorkSystemPermission.ControlSystem,
                system.Id,
                system.Name);
        }
    }
}
