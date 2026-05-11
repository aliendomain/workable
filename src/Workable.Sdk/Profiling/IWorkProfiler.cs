using System.Runtime.CompilerServices;

namespace Workable;

public interface IWorkProfiler
{
    void AddInfo(string name, object? context = null);

    IWorkProfileScope StartTiming(string name, object? context = null);

    IWorkProfileScope CreateScope(string name, object? context = null);

    IWorkProfileScope CreateMethodScope(
        Type type,
        string methodName,
        object? context = null,
        string label = "Input");

    IWorkProfileScope CreateMethodScope<T>(
        object? context = null,
        string label = "Input",
        [CallerMemberName] string methodName = "");
}
