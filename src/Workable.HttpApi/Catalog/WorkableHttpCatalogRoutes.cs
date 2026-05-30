using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

internal static class WorkableHttpCatalogRoutes
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/definitions", async (
            HttpContext httpContext,
            string? category,
            bool? includeSubcategories,
            bool? level,
            WorkableHttpTopologyResolver topology,
            WorkableHttpCatalogAdapter catalog,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                "Browse work definitions through HTTP API.");
            if (level == true)
            {
                return Results.Ok(WorkableHttpCatalogAdapter.GetDefinitionCatalogLevel(session.Catalog, category));
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                var definitions = await session.Query.WorkDefinitions(new WorkDefinitionCriteria(
                        Category: category,
                        IncludeSubcategories: includeSubcategories ?? true), cancellationToken: cancellationToken);
                return Results.Ok(definitions.Definitions);
            }

            return Results.Ok(catalog.GetDefinitions(session));
        });

        group.MapGet("/definitions/{definitionId:guid}", (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpTopologyResolver topology,
            WorkableHttpCatalogAdapter catalog,
            IWorkRequestContextFactory requestContexts) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                "Read a work definition through HTTP API.");
            var definition = catalog.GetDefinition(session, new WorkDefinitionId(definitionId));
            return definition is null ? Results.NotFound() : Results.Ok(definition);
        });

        group.MapPost("/definitions/{definitionId:guid}/reconfigure", async (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpDefinitionReconfigurationRequest request,
            WorkableHttpTopologyResolver topology,
            WorkableHttpCatalogAdapter catalog,
            IWorkRequestContextFactory requestContexts,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts,
                "Reconfigure work definition through HTTP API.");
            var result = await catalog.ReconfigureDefinition(
                session,
                new WorkDefinitionId(definitionId),
                request,
                cancellationToken);
            return WorkableHttpRouteResults.ToDefinitionReconfigurationHttpResult(result);
        });
    }
}
