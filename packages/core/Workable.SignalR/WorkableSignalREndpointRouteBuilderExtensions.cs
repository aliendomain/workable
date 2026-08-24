using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Maps the Workable SignalR hub into an ASP.NET Core endpoint route builder.
/// </summary>
public static class WorkableSignalREndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the authenticated Workable realtime hub.
    /// </summary>
    /// <param name="endpoints">The route builder that exposes the hub endpoint.</param>
    /// <param name="path">
    /// Optional hub path override. When omitted, the configured <see cref="WorkableSignalROptions.HubPath"/> is used.
    /// </param>
    /// <param name="advertise">
    /// Whether capability discovery should advertise this mapping. Set this to <see langword="false"/> for aliases.
    /// </param>
    /// <param name="authorizationPolicy">
    /// Optional host-defined authorization policy for this mapping. When omitted, the host's default policy applies.
    /// </param>
    /// <param name="useHostFallbackPolicy">
    /// Whether to leave the endpoint without authorization metadata so the host's fallback policy applies.
    /// </param>
    /// <returns>The endpoint convention builder for further endpoint customization.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the configured Workable realtime settings are invalid or the host's effective hub protocol is
    /// incompatible with the selected Workable payload serializer.
    /// </exception>
    /// <remarks>
    /// Workable SignalR requires authorization-enabled systems and an authenticated Workable connection. An explicitly
    /// selected transport authentication scheme is evaluated only for Workable and does not replace the host's ambient principal.
    /// When <paramref name="useHostFallbackPolicy"/> is true, the host remains responsible for registering and applying
    /// an appropriate fallback policy through its normal authorization middleware.
    /// </remarks>
    public static IEndpointConventionBuilder MapWorkableSignalR(
        this IEndpointRouteBuilder endpoints,
        string? path = null,
        bool advertise = true,
        string? authorizationPolicy = null,
        bool useHostFallbackPolicy = false)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<WorkableSignalROptions>>().Value;
        WorkableSignalROptionsValidation.ThrowIfInvalidRealtime(options);
        EnsureProtocolCompatibility(endpoints.ServiceProvider);
        path ??= options.HubPath;

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (authorizationPolicy is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        }
        if (authorizationPolicy is not null && useHostFallbackPolicy)
        {
            throw new ArgumentException(
                "A named authorization policy and the host fallback policy cannot both be selected.",
                nameof(useHostFallbackPolicy));
        }

        var builder = endpoints.MapHub<WorkableRealtimeHub>(
            path,
            dispatcher => dispatcher.CloseOnAuthenticationExpiration = true);
        builder.WithMetadata(new WorkableSignalREndpointMetadata());
        if (authorizationPolicy is not null)
        {
            builder.RequireAuthorization(authorizationPolicy);
        }
        else if (!useHostFallbackPolicy)
        {
            builder.RequireAuthorization();
        }
        if (advertise)
        {
            endpoints.ServiceProvider
                .GetRequiredService<WorkableSignalRRegistration>()
                .Advertise(path);
        }
        else
        {
            endpoints.ServiceProvider
                .GetRequiredService<WorkableSignalRRegistration>()
                .MarkMapped();
        }

        return builder;
    }

    private static void EnsureProtocolCompatibility(IServiceProvider services)
    {
        if (services.GetRequiredService<IWorkableSignalRPayloadSerializer>() is not
            WorkableSignalRJsonPayloadSerializer)
        {
            return;
        }

        var protocols = services
            .GetRequiredService<IOptions<HubOptions<WorkableRealtimeHub>>>()
            .Value
            .SupportedProtocols;
        if (protocols is { Count: 1 } &&
            string.Equals(protocols[0], "json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            "The default Workable SignalR payload serializer requires the host to configure the Workable hub for only the 'json' protocol. " +
            "Configure HubOptions<WorkableRealtimeHub>.SupportedProtocols or replace IWorkableSignalRPayloadSerializer for the host-selected protocol.");
    }
}
