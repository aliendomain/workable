using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkEventSubscription : IAsyncDisposable
{
    IAsyncEnumerable<WorkEvent> Read(CancellationToken cancellationToken = default);
}
