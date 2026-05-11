using Workable;

namespace Workable.Tests;

internal static class WorkerOptionFixtures
{
    public static WorkerOptions DoNotStart(WorkConfiguration? configuration = null)
        => new(Configuration: (configuration ?? WorkConfiguration.Default) with
        {
            Start = WorkStartConfiguration.DoNotStart,
        });
}
