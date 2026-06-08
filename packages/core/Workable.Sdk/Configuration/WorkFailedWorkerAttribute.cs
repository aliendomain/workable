namespace Workable;

/// <summary>
/// Declares default failed-worker handling for a work executor.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkFailedWorkerAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkFailedWorkerAttribute"/> class.
    /// </summary>
    /// <param name="handling">Whether failed workers should require manual handling or be auto-canceled.</param>
    /// <param name="autoCancelAfterSeconds">
    /// The failed-state delay, in seconds, before auto-cancel occurs when <paramref name="handling"/> is
    /// <see cref="WorkFailedWorkerHandling.AutoCancel"/>.
    /// </param>
    public WorkFailedWorkerAttribute(
        WorkFailedWorkerHandling handling = WorkFailedWorkerHandling.Manual,
        int autoCancelAfterSeconds = 600)
    {
        this.Configuration = new WorkFailedWorkerConfiguration
        {
            Handling = handling,
            AutoCancelAfter = TimeSpan.FromSeconds(autoCancelAfterSeconds),
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { FailedWorker = this.Configuration });
    }

    /// <summary>
    /// Gets the validated failed-worker handling configuration produced by the attribute.
    /// </summary>
    public WorkFailedWorkerConfiguration Configuration { get; }
}
