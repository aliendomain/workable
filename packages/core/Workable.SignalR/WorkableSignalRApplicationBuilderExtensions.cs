using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Adds Workable SignalR request preprocessing to an ASP.NET Core pipeline.
/// </summary>
public static class WorkableSignalRApplicationBuilderExtensions
{
    /// <summary>
    /// Promotes valid SignalR query-string access tokens to the standard Authorization header on mapped Workable hubs.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder for chaining.</returns>
    /// <remarks>
    /// When the host does not already extract SignalR query-string tokens, call this after routing and before the
    /// host's authentication and authorization middleware. Workable does not select or configure an authentication
    /// handler; the host's configured handler validates the promoted token. Query-token middleware settings are
    /// validated when this optional middleware is added, not when the hub is mapped.
    /// </remarks>
    public static IApplicationBuilder UseWorkableSignalRAccessTokens(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices
            .GetRequiredService<IOptions<WorkableSignalROptions>>()
            .Value;
        if (options.PromoteAccessTokensFromQueryString &&
            string.IsNullOrWhiteSpace(options.AccessTokenQueryStringName))
        {
            throw new InvalidOperationException(
                "Workable SignalR requires a non-empty access token query string name when query-token promotion is enabled.");
        }

        return app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsOptions(context.Request.Method) &&
                context.GetEndpoint()?.Metadata.GetMetadata<WorkableSignalREndpointMetadata>() is not null)
            {
                WorkableSignalRAccessToken.TryPromote(context, options);
            }

            await next(context);
        });
    }
}

internal sealed class WorkableSignalREndpointMetadata;
