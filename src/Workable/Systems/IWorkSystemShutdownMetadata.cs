namespace Workable;

internal interface IWorkSystemShutdownMetadata
{
    TimeSpan ShutdownGracePeriod { get; }
}
