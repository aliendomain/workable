namespace Workable;

internal interface IWorkAutomaticProfileSamplingGate
{
    bool TryReserveAutomaticNodeForSampling(string instrumentation);

    bool TryStartReservedAutomaticTiming<TContext>(
        string name,
        Func<TContext> contextFactory,
        out TContext? context,
        out IWorkProfileScope? scope)
        where TContext : class;

    void ReleaseReservedAutomaticNode();
}
