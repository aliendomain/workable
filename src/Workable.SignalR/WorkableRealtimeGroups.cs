namespace Workable;
internal static class WorkableRealtimeGroups
{
    public static string Dashboard(IWorkSystem system)
        => $"{System(system)}:dashboard";

    public static string SystemEvents(IWorkSystem system)
        => $"{System(system)}:events";

    public static string Worker(IWorkSystem system, WorkerId workerId)
        => $"{System(system)}:worker:{workerId.Value:N}";

    public static string Definition(IWorkSystem system, WorkDefinitionId definitionId)
        => $"{System(system)}:definition:{definitionId.Value:N}";

    private static string System(IWorkSystem system)
        => $"workable:{system.Id.Value:N}";
}
