namespace Workable;

internal interface IWorkSystemBuiltInHttpSurfaceAccess
{
    bool IsBuiltInHttpSurfaceAllowed(WorkRequestContext requestContext);
}
