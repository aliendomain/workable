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
            WorkableHttpSystemResolver systems,
            WorkableHttpCatalogAdapter catalog,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            if (level == true)
            {
                return Results.Ok(WorkableHttpCatalogAdapter.GetDefinitionCatalogLevel(system, category));
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                var definitions = await system.Query.WorkDefinitions(new WorkDefinitionCriteria(
                        Category: category,
                        IncludeSubcategories: includeSubcategories ?? true), cancellationToken: cancellationToken);
                return Results.Ok(definitions.Definitions);
            }

            return Results.Ok(catalog.GetDefinitions(system));
        });

        group.MapPost("/definitions/{definitionId:guid}/reconfigure", async (
            HttpContext httpContext,
            Guid definitionId,
            WorkableHttpDefinitionReconfigurationRequest request,
            WorkableHttpSystemResolver systems,
            WorkableHttpCatalogAdapter catalog,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, systems, out var system, out var notFound))
            {
                return notFound;
            }

            var result = await catalog.ReconfigureDefinition(
                system,
                new WorkDefinitionId(definitionId),
                request,
                cancellationToken);
            return WorkableHttpRouteResults.ToDefinitionReconfigurationHttpResult(result);
        });
    }
}
