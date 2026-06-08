using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Declares the default start policy for a work definition.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkStartAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkStartAttribute"/> class.
    /// </summary>
    /// <param name="policy">The default start policy to apply to the definition.</param>
    public WorkStartAttribute(WorkStartPolicy policy = WorkStartPolicy.StartAndReturnAfterAccepted)
    {
        this.Configuration = new WorkStartConfiguration
        {
            Policy = policy,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Start = this.Configuration });
    }

    /// <summary>
    /// Gets the start configuration implied by the attribute.
    /// </summary>
    public WorkStartConfiguration Configuration { get; }
}
