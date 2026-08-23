using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Text.Json;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

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

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts);
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

        group.MapGet("/definitions/{name}", async (
            HttpContext httpContext,
            string name,
            WorkableHttpTopologyResolver topology,
            WorkableHttpCatalogAdapter catalog,
            IWorkRequestContextFactory requestContexts) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts);
            var definition = catalog.GetDefinition(session, name);
            return definition is null ? Results.NotFound() : Results.Ok(definition);
        });

        group.MapPost("/definitions/{name}/reconfigure", async (
            HttpContext httpContext,
            string name,
            JsonElement requestBody,
            WorkableHttpTopologyResolver topology,
            WorkableHttpCatalogAdapter catalog,
            IWorkRequestContextFactory requestContexts,
            IOptions<HttpJsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            if (!WorkableHttpRouteResults.TryResolveSystem(httpContext, topology, out var system, out var notFound))
            {
                return notFound;
            }

            WorkableHttpDefinitionReconfigurationRequest request;
            try
            {
                request = WorkableHttpReconfigurationJson.ParseDefinition(
                    requestBody,
                    jsonOptions.Value.SerializerOptions);
            }
            catch (WorkableHttpReconfigurationValidationException exception)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error("workable.http.reconfiguration.invalid", exception.Message, "request"),
                    },
                });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new
                {
                    Messages = new[]
                    {
                        WorkMessage.Error(
                            "workable.http.reconfiguration.invalid",
                            "The definition reconfiguration request contains invalid JSON values.",
                            "request"),
                    },
                });
            }

            var session = await WorkableHttpRequestContext.CreateSession(
                httpContext,
                system,
                requestContexts);
            var result = await catalog.ReconfigureDefinition(
                session,
                name,
                request,
                cancellationToken);
            return WorkableHttpRouteResults.ToDefinitionReconfigurationHttpResult(result);
        });
    }
}
