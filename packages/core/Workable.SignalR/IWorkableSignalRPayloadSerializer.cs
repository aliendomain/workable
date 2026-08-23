using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Serializes Workable realtime payloads before they are handed to the host-selected SignalR protocol.
/// </summary>
/// <remarks>
/// The default serializer produces JSON values using the host's <see cref="JsonHubProtocolOptions"/> and a
/// package-local string-enum fallback. A host that selects a different or custom SignalR protocol can replace this
/// service so the protocol receives the representation it expects without Workable changing protocol settings.
/// </remarks>
public interface IWorkableSignalRPayloadSerializer
{
    /// <summary>
    /// Produces the value passed to the selected SignalR protocol for one Workable realtime payload.
    /// </summary>
    /// <typeparam name="T">The Workable payload type.</typeparam>
    /// <param name="value">The payload to serialize.</param>
    /// <returns>The protocol-facing payload value.</returns>
    object? Serialize<T>(T value);
}

internal sealed class WorkableSignalRJsonPayloadSerializer(
    IOptions<JsonHubProtocolOptions> jsonProtocolOptions) : IWorkableSignalRPayloadSerializer
{
    private readonly System.Text.Json.JsonSerializerOptions options =
        WorkableSignalRJson.CreateOptions(jsonProtocolOptions.Value.PayloadSerializerOptions);

    public object Serialize<T>(T value)
        => WorkableSignalRJson.Serialize(value, this.options);
}
