namespace Workable;

internal interface IWorkSystemBuiltInHttpSurfaceAccess
{
    ValueTask<bool> IsBuiltInHttpSurfaceAllowed(
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default);
}
