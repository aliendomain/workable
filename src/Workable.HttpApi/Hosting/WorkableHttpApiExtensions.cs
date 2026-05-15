using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Workable;

public static class WorkableHttpApiExtensions
{
    public static IEndpointRouteBuilder MapWorkableApi(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/workable")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = endpoints.MapGroup(prefix);
        group.MapGet("/systems", (WorkableHttpSystemResolver systems)
            => Results.Ok(systems.GetSystems()));

        MapWorkableApiRoutes(group);
        MapWorkableApiRoutes(group.MapGroup("/systems/{systemName}"));

        return endpoints;
    }

    private static void MapWorkableApiRoutes(RouteGroupBuilder group)
    {
        WorkableHttpSystemRoutes.Map(group);
        WorkableHttpCatalogRoutes.Map(group);
        WorkableHttpQueueRoutes.Map(group);
        WorkableHttpQueryRoutes.Map(group);
        WorkableHttpWorkerRoutes.Map(group);
    }
}
