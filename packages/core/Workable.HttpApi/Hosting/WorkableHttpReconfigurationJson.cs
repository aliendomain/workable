using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

internal static class WorkableHttpReconfigurationJson
{
    public static WorkableHttpDefinitionReconfigurationRequest ParseDefinition(
        JsonElement value,
        JsonSerializerOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        var properties = ReadProperties(
            value,
            "request",
            "revision",
            "changes");
        var revision = ReadRequiredRevision(properties);
        var changes = DeserializeRequiredObject<WorkDefinitionReconfiguration>(
            properties,
            "changes",
            hostOptions);
        if (changes.DefaultOptions is null && changes.Configuration is null)
        {
            throw new WorkableHttpReconfigurationValidationException(
                "Definition reconfiguration requires at least one of 'defaultOptions' or 'configuration'.");
        }

        return new WorkableHttpDefinitionReconfigurationRequest(revision, changes);
    }

    public static WorkableHttpWorkerReconfigurationRequest ParseWorker(
        JsonElement value,
        JsonSerializerOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        var properties = ReadProperties(
            value,
            "request",
            "revision",
            "changes",
            "description");
        var revision = ReadRequiredRevision(properties);
        var changes = DeserializeRequiredObject<WorkerReconfiguration>(
            properties,
            "changes",
            hostOptions);
        if (!HasChanges(changes))
        {
            throw new WorkableHttpReconfigurationValidationException("Worker reconfiguration requires at least one change.");
        }

        string? description = null;
        if (properties.TryGetValue("description", out var descriptionValue))
        {
            description = descriptionValue.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => descriptionValue.GetString(),
                _ => throw new WorkableHttpReconfigurationValidationException("Property 'description' must be a string or null."),
            };
        }

        return new WorkableHttpWorkerReconfigurationRequest(revision, changes, description);
    }

    private static Dictionary<string, JsonElement> ReadProperties(
        JsonElement value,
        string path,
        params string[] supportedProperties)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new WorkableHttpReconfigurationValidationException($"Property '{path}' must be an object.");
        }

        RejectDuplicatePropertiesRecursively(value, path);
        var supported = supportedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!supported.Contains(property.Name))
            {
                throw new WorkableHttpReconfigurationValidationException($"Property '{path}' contains unsupported property '{property.Name}'.");
            }

            properties.Add(property.Name, property.Value.Clone());
        }

        return properties;
    }

    private static long ReadRequiredRevision(IReadOnlyDictionary<string, JsonElement> properties)
        => properties.TryGetValue("revision", out var revision) &&
            revision.ValueKind == JsonValueKind.Number &&
            revision.TryGetInt64(out var value)
            ? value
            : throw new WorkableHttpReconfigurationValidationException("Required property 'revision' is missing or invalid.");

    private static T DeserializeRequiredObject<T>(
        IReadOnlyDictionary<string, JsonElement> properties,
        string propertyName,
        JsonSerializerOptions hostOptions)
    {
        if (!properties.TryGetValue(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new WorkableHttpReconfigurationValidationException($"Required property '{propertyName}' must be an object.");
        }

        var strictOptions = new JsonSerializerOptions(hostOptions)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
        };
        return value.Deserialize<T>(strictOptions)
            ?? throw new WorkableHttpReconfigurationValidationException($"Property '{propertyName}' is invalid.");
    }

    private static bool HasChanges(WorkerReconfiguration changes)
        => changes.ProfilingEnabled is not null ||
            changes.ProfilingCaptureMode is not null ||
            changes.Start is not null ||
            changes.Coordination is not null ||
            changes.Recurrence is not null ||
            changes.TransientRetry is not null ||
            changes.FailedWorker is not null ||
            changes.Logging is not null ||
            changes.Retention is not null;

    private static void RejectDuplicatePropertiesRecursively(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicatePropertiesRecursively(item, $"{path}[{index}]");
                index++;
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new WorkableHttpReconfigurationValidationException($"Property '{path}' contains duplicate property '{property.Name}'.");
            }

            RejectDuplicatePropertiesRecursively(property.Value, $"{path}.{property.Name}");
        }
    }
}

internal sealed class WorkableHttpReconfigurationValidationException(string message)
    : JsonException(message);
