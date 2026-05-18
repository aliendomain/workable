namespace Workable;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkQueueDurabilityAttribute : Attribute
{
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

    public WorkQueueDurabilityConfiguration Configuration { get; }
}
