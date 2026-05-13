namespace Workable;
public interface IWorkRealtimeCapabilityProvider
{
    WorkRealtimeCapability GetCapability(IWorkSystem system);
}
