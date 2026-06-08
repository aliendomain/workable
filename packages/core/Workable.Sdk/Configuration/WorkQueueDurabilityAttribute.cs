namespace Workable;

/// <summary>
/// Declares default durable queue behavior for a work executor.
/// </summary>
/// <remarks>
/// Applying this attribute switches coordination storage to persistent mode during registration. Use fluent
/// configuration when a host needs to adjust polling or durable completion for a specific registration.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkQueueDurabilityAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkQueueDurabilityAttribute"/> class.
    /// </summary>
    /// <param name="isEnabled">Enables durable queue persistence.</param>
    /// <param name="completeDurably">
    /// Requires successful execution to call <c>IWorkExecutionContext.CompleteDurably(...)</c> before returning.
    /// </param>
    /// <param name="fallbackPollingSeconds">
    /// The fallback polling interval, in seconds, used when durable work is not discovered through an immediate signal.
    /// </param>
    public WorkQueueDurabilityAttribute(
        bool isEnabled = true,
        bool completeDurably = false,
        int fallbackPollingSeconds = 5)
    {
        this.Configuration = new WorkQueueDurabilityConfiguration
        {
            IsEnabled = isEnabled,
            CompleteDurably = completeDurably,
            FallbackPollingInterval = TimeSpan.FromSeconds(fallbackPollingSeconds),
        };
    }

    /// <summary>
    /// Gets the durable queue configuration produced by the attribute.
    /// </summary>
    public WorkQueueDurabilityConfiguration Configuration { get; }
}
