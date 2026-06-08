using System.Diagnostics.CodeAnalysis;

namespace Workable;

/// <summary>
/// Specifies how a bounded event subscription buffer behaves when it overflows.
/// </summary>
public enum WorkEventOverflowBehavior
{
    /// <summary>
    /// Evict the oldest buffered event to make room for the newest event.
    /// </summary>
    DropOldest,

    /// <summary>
    /// Drop the newest incoming event while keeping the existing buffer contents.
    /// </summary>
    DropNewest,

    /// <summary>
    /// Reject the write rather than mutating the buffer contents.
    /// </summary>
    DropWrite,
}
