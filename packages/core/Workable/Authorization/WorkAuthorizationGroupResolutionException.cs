namespace Workable;

internal sealed class WorkAuthorizationGroupResolutionException(
    string? systemName,
    Exception innerException)
    : Exception(
        $"Authorization groups could not be resolved for Workable system '{systemName ?? "<default>"}'.",
        innerException)
{
    internal static bool CanWrap(Exception exception)
        => exception is not (
            OperationCanceledException or
            OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            ThreadAbortException or
            InvalidProgramException or
            WorkAuthorizationGroupResolutionException);
}
