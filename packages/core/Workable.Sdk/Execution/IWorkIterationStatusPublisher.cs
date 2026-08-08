using System.Text.Json;

namespace Workable;

/// <summary>
/// Publishes ordered, application-defined status items for the currently executing work iteration.
/// </summary>
/// <remarks>
/// Status items are intended for transient progress such as assistant text deltas, tool activity, or stage updates.
/// They are separate from worker lifecycle events and retained work output.
/// </remarks>
public interface IWorkIterationStatusPublisher
{
    /// <summary>
    /// Publishes one status item without a payload.
    /// </summary>
    /// <param name="type">The application-defined status type.</param>
    void Publish(string type)
        => this.Publish(new WorkIterationStatusUpdate(type, Data: null));

    /// <summary>
    /// Publishes one status item with a JSON-serializable payload.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="type">The application-defined status type.</param>
    /// <param name="data">The payload to serialize.</param>
    /// <param name="options">Optional JSON serializer options.</param>
    void Publish<T>(string type, T data, JsonSerializerOptions? options = null)
        => this.Publish(WorkIterationStatusUpdate.FromValue(type, data, options));

    /// <summary>
    /// Publishes one pre-serialized status update.
    /// </summary>
    /// <param name="update">The status update to publish.</param>
    void Publish(WorkIterationStatusUpdate update);
}

internal sealed class EmptyWorkIterationStatusPublisher : IWorkIterationStatusPublisher
{
    public static EmptyWorkIterationStatusPublisher Instance { get; } = new();

    private EmptyWorkIterationStatusPublisher()
    {
    }

    public void Publish(WorkIterationStatusUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Type);
    }
}
