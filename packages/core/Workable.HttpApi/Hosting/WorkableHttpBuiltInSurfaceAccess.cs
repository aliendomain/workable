namespace Workable;

internal static class WorkableHttpBuiltInSurfaceAccess
{
    internal static async ValueTask<bool> IsAllowed(
        IWorkSystem system,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (system is IWorkSystemBuiltInHttpSurfaceAccess fastPath)
        {
            return await fastPath.IsBuiltInHttpSurfaceAllowed(requestContext, cancellationToken);
        }

        return IsAllowed(await system.DescribeAccess(requestContext, cancellationToken));
    }

    internal static bool IsAllowed(WorkSystemAccessSummary access)
    {
        ArgumentNullException.ThrowIfNull(access);

        return access.IsSystemAdministrator || access.IsWorkAdministrator;
    }
}
