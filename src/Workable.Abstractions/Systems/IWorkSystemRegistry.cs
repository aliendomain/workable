using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkSystemRegistry
{
    IWorkSystem Default { get; }

    IReadOnlyCollection<IWorkSystem> Systems { get; }

    bool TryGet(string name, [NotNullWhen(true)] out IWorkSystem? workSystem);
}
