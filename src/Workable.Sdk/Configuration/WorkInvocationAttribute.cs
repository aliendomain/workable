namespace Workable;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkInvocationAttribute : Attribute
{
    public WorkInvocationAttribute(params WorkInvocationChannel[] allowedChannels)
    {
        ArgumentNullException.ThrowIfNull(allowedChannels);

        this.AllowedChannels = allowedChannels.ToHashSet();
        this.Configuration = WorkInvocationConfiguration.Default.AllowAdditional(allowedChannels);
        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Invocation = this.Configuration });
    }

    public IReadOnlySet<WorkInvocationChannel> AllowedChannels { get; }

    public WorkInvocationConfiguration Configuration { get; }
}
