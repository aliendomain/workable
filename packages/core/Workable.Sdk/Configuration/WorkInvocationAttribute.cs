namespace Workable;

/// <summary>
/// Declares additional invocation channels that may start a work definition.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkInvocationAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkInvocationAttribute"/> class.
    /// </summary>
    /// <param name="allowedChannels">The additional invocation channels to allow for the definition.</param>
    public WorkInvocationAttribute(params WorkInvocationChannel[] allowedChannels)
    {
        ArgumentNullException.ThrowIfNull(allowedChannels);

        this.AllowedChannels = allowedChannels.ToHashSet();
        this.Configuration = WorkInvocationConfiguration.Default.AllowAdditional(allowedChannels);
        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Invocation = this.Configuration });
    }

    /// <summary>
    /// Gets the additional invocation channels allowed by the attribute.
    /// </summary>
    public IReadOnlySet<WorkInvocationChannel> AllowedChannels { get; }

    /// <summary>
    /// Gets the composed invocation configuration implied by the attribute.
    /// </summary>
    public WorkInvocationConfiguration Configuration { get; }
}
