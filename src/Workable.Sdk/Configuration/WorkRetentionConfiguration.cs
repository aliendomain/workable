using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;
public sealed record WorkRetentionConfiguration
{
    public static WorkRetentionConfiguration Default { get; } = new();

    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromMinutes(5);
}
