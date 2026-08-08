using System.Text.Json;

namespace Workable;

/// <summary>
/// Represents application-defined data to append to the current iteration status stream.
/// </summary>
/// <param name="Type">The application-defined status type, such as <c>assistant.text.delta</c>.</param>
/// <param name="Data">The optional structured payload.</param>
public sealed record WorkIterationStatusUpdate(string Type, JsonElement? Data)
{
    /// <summary>
    /// Creates an update by serializing a typed payload.
    /// </summary>
    public static WorkIterationStatusUpdate FromValue<T>(
        string type,
        T data,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return new WorkIterationStatusUpdate(
            type.Trim(),
            JsonSerializer.SerializeToElement(data, options ?? WorkData.DefaultJsonOptions));
    }
}
