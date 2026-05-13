using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable;
public static class WorkableSignalREndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapWorkableSignalR(
        this IEndpointRouteBuilder endpoints,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<WorkableSignalROptions>>().Value;
        if (path is null)
        {
            path = options.HubPath;
        }
        else
        {
            options.HubPath = path;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return endpoints.MapHub<WorkableRealtimeHub>(path);
    }
}
