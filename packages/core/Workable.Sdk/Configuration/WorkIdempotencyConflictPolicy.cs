using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Defines how Workable responds when idempotency detects another active worker for the same subject.
/// </summary>
public enum WorkIdempotencyConflictPolicy
{
    /// <summary>
    /// Rejects the queue request instead of accepting a duplicate worker.
    /// </summary>
    RejectDuplicates,
}
