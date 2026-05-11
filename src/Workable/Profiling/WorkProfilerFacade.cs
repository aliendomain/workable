namespace Workable;

internal sealed class WorkProfilerFacade : IWorkProfiler
{
    private static IWorkProfiler Current => WorkProfilerContext.Current ?? NoOpWorkProfiler.Instance;

    public void AddInfo(string name, object? context = null)
        => Current.AddInfo(name, context);

    public IWorkProfileScope StartTiming(string name, object? context = null)
        => Current.StartTiming(name, context);

    public IWorkProfileScope CreateScope(string name, object? context = null)
        => Current.CreateScope(name, context);

    public IWorkProfileScope CreateMethodScope(Type type, string methodName, object? context = null, string label = "Input")
        => Current.CreateMethodScope(type, methodName, context, label);

    public IWorkProfileScope CreateMethodScope<T>(object? context = null, string label = "Input", string methodName = "")
        => Current.CreateMethodScope<T>(context, label, methodName);
}
