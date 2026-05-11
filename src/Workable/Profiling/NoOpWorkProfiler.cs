namespace Workable;

internal sealed class NoOpWorkProfiler : IWorkProfiler
{
    public static NoOpWorkProfiler Instance { get; } = new();

    private NoOpWorkProfiler()
    {
    }

    public void AddInfo(string name, object? context = null)
    {
    }

    public IWorkProfileScope StartTiming(string name, object? context = null)
        => NoOpWorkProfileScope.Instance;

    public IWorkProfileScope CreateScope(string name, object? context = null)
        => NoOpWorkProfileScope.Instance;

    public IWorkProfileScope CreateMethodScope(Type type, string methodName, object? context = null, string label = "Input")
        => NoOpWorkProfileScope.Instance;

    public IWorkProfileScope CreateMethodScope<T>(object? context = null, string label = "Input", string methodName = "")
        => NoOpWorkProfileScope.Instance;

    private sealed class NoOpWorkProfileScope : IWorkProfileScope
    {
        public static NoOpWorkProfileScope Instance { get; } = new();

        public void Dispose()
        {
        }

        public void SetResult(object? context = null)
        {
        }
    }
}
