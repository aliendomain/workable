using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class WorkStartAttribute : Attribute
{
    public WorkStartAttribute(WorkStartPolicy policy = WorkStartPolicy.StartAndReturnAfterAccepted)
    {
        this.Configuration = new WorkStartConfiguration
        {
            Policy = policy,
        };

        WorkConfigurationValidator.ThrowIfInvalid(WorkConfiguration.Default with { Start = this.Configuration });
    }

    public WorkStartConfiguration Configuration { get; }
}
