using System.Diagnostics.CodeAnalysis;

namespace Workable;
public enum WorkEventOverflowBehavior
{
    DropOldest,
    DropNewest,
    DropWrite,
}
