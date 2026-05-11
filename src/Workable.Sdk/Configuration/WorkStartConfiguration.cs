using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public sealed record WorkStartConfiguration
{
    public static WorkStartConfiguration Default { get; } = new();

    public static WorkStartConfiguration DoNotStart { get; } = new()
    {
        Policy = WorkStartPolicy.DoNotStart,
    };

    public WorkStartPolicy Policy { get; init; } = WorkStartPolicy.StartAndReturnAfterAccepted;
}
