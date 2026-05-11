namespace Workable;

public interface IWorkProfileScope : IDisposable
{
    void SetResult(object? context = null);
}
