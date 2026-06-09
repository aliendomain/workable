namespace Workable;

internal static class WorkableHttpBuiltInSurfaceAccess
{
    internal static bool IsAllowed(
        IWorkSystem system,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (system is IWorkSystemBuiltInHttpSurfaceAccess fastPath)
        {
            return fastPath.IsBuiltInHttpSurfaceAllowed(requestContext);
        }

        return IsAllowed(system.DescribeAccess(requestContext));
    }

    internal static bool IsAllowed(WorkSystemAccessSummary access)
    {
        ArgumentNullException.ThrowIfNull(access);

        return access.IsSystemAdministrator || access.IsWorkAdministrator;
    }
}
